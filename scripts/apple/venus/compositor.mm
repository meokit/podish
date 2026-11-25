#include "compositor.h"

#define VK_USE_PLATFORM_METAL_EXT 1
#define VOLK_NAMESPACE 1
#include <volk.h>
#include "vulkan-dispatch.h"

#include "Common/interface/RefCntAutoPtr.hpp"
#include "Graphics/GraphicsEngine/interface/Buffer.h"
#include "Graphics/GraphicsEngine/interface/DeviceContext.h"
#include "Graphics/GraphicsEngine/interface/Fence.h"
#include "Graphics/GraphicsEngine/interface/PipelineState.h"
#include "Graphics/GraphicsEngine/interface/Sampler.h"
#include "Graphics/GraphicsEngine/interface/ShaderResourceBinding.h"
#include "Graphics/GraphicsEngine/interface/ShaderResourceVariable.h"
#include "Graphics/GraphicsEngine/interface/SwapChain.h"
#include "Graphics/GraphicsEngine/interface/Texture.h"
#include "Graphics/GraphicsEngineVulkan/interface/EngineFactoryVk.h"
#include "Graphics/GraphicsEngineVulkan/interface/RenderDeviceVk.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <exception>
#include <memory>
#include <unordered_set>
#include <vector>

#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

extern "C" VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
podish_diligent_get_instance_proc_addr(VkInstance instance, const char* name)
{
    return podish_vk_get_instance_proc_addr(instance, name);
}

namespace {
using namespace Diligent;

thread_local char lastError[512];

void setError(const char* message)
{
    std::snprintf(lastError, sizeof(lastError), "%s", message);
}

void setVulkanError(const char* operation, VkResult result)
{
    std::snprintf(lastError, sizeof(lastError), "%s failed (VkResult %d)", operation, result);
}

template <typename Result, typename Operation>
Result protect(Result failure, Operation op) noexcept
{
    try {
        return op();
    } catch (const std::exception& ex) {
        setError(ex.what());
    } catch (...) {
        setError("Unexpected Vulkan compositor error");
    }
    return failure;
}

constexpr uint32_t vertexShader[] = {
#include "compositor-vertex.spv.inc"
};
constexpr uint32_t imageShader[] = {
#include "compositor-image.spv.inc"
};
constexpr uint32_t bufferShader[] = {
#include "compositor-buffer.spv.inc"
};

struct Parameters {
    float rect[4];
    float color[4];
    float titleColor[4];
    float originAndViewport[4];
    float uvBasis[4];
    float chrome[4];
    float drawOptions[4];
    uint32_t source[4];
};

static_assert(sizeof(Parameters) == 128);
static_assert(sizeof(struct podish_compositor_draw) == 96);

struct Texture {
    RefCntAutoPtr<ITexture> image;
    RefCntAutoPtr<IBuffer> buffer;
    RefCntAutoPtr<IShaderResourceBinding> bindings;
    VkDevice vkDevice = VK_NULL_HANDLE;
    VkBuffer vkBuffer = VK_NULL_HANDLE;
    VkDeviceMemory vkMemory = VK_NULL_HANDLE;
    void* mapping = MAP_FAILED;
    size_t mappingSize = 0;
    uint64_t lastUsed = 0;
    uint32_t width;
    uint32_t height;
    uint32_t strideWords = 0;
    uint32_t offsetWords = 0;
    int format;

    Texture(int width, int height, int format) : width(width), height(height), format(format) {}

    ~Texture()
    {
        bindings.Release();
        image.Release();
        buffer.Release();
        if (vkBuffer != VK_NULL_HANDLE)
            vkDestroyBuffer(vkDevice, vkBuffer, nullptr);
        if (vkMemory != VK_NULL_HANDLE)
            vkFreeMemory(vkDevice, vkMemory, nullptr);
        if (mapping != MAP_FAILED)
            munmap(mapping, mappingSize);
    }
};

struct Compositor {
    RefCntAutoPtr<IRenderDevice> device;
    RefCntAutoPtr<IDeviceContext> context;
    RefCntAutoPtr<ISwapChain> swapChain;
    RefCntAutoPtr<IFence> fence;
    RefCntAutoPtr<IBuffer> uniforms;
    RefCntAutoPtr<IPipelineState> imagePipeline;
    RefCntAutoPtr<IPipelineState> bufferPipeline;
    RefCntAutoPtr<ISampler> linear;
    RefCntAutoPtr<ISampler> nearest;
    std::unique_ptr<Texture> emptyTexture;
    std::unordered_set<Texture*> textures;
    std::vector<std::unique_ptr<Texture>> retired;
    VkDevice vkDevice = VK_NULL_HANDLE;
    VkPhysicalDevice physicalDevice = VK_NULL_HANDLE;
    VkDeviceSize hostPointerAlignment = 0;
    uint32_t coherentMemoryTypes = 0;
    VkDeviceSize maxStorageBufferRange = 0;
    uint64_t submitted = 0;
    bool inFrame = false;

    ~Compositor()
    {
        if (context)
            context->Flush();
        if (device)
            device->IdleGPU();
        for (auto* texture : textures)
            delete texture;
        retired.clear();
    }

    uint64_t collect()
    {
        const uint64_t complete = fence ? fence->GetCompletedValue() : 0;
        retired.erase(std::remove_if(retired.begin(), retired.end(),
                       [complete](const auto& texture) { return texture->lastUsed <= complete; }),
                      retired.end());
        return complete;
    }

    RefCntAutoPtr<IShader> shader(SHADER_TYPE stage, const char* name,
                                 const uint32_t* code, size_t bytes)
    {
        ShaderCreateInfo info;
        info.Desc.Name = name;
        info.Desc.ShaderType = stage;
        info.SourceLanguage = SHADER_SOURCE_LANGUAGE_GLSL;
        info.EntryPoint = "main";
        info.ByteCode = code;
        info.ByteCodeSize = bytes;
        RefCntAutoPtr<IShader> result;
        device->CreateShader(info, &result);
        return result;
    }

    RefCntAutoPtr<IPipelineState> pipeline(IShader* vertex, IShader* fragment, const char* name)
    {
        GraphicsPipelineStateCreateInfo info;
        info.PSODesc.Name = name;
        info.PSODesc.PipelineType = PIPELINE_TYPE_GRAPHICS;
        info.pVS = vertex;
        info.pPS = fragment;
        ShaderResourceVariableDesc uniform{SHADER_TYPE_VERTEX | SHADER_TYPE_PIXEL, "Parameters",
                                           SHADER_RESOURCE_VARIABLE_TYPE_STATIC};
        auto& layout = info.PSODesc.ResourceLayout;
        layout.DefaultVariableType = SHADER_RESOURCE_VARIABLE_TYPE_MUTABLE;
        layout.Variables = &uniform;
        layout.NumVariables = 1;
        auto& graphics = info.GraphicsPipeline;
        graphics.NumRenderTargets = 1;
        graphics.RTVFormats[0] = swapChain->GetDesc().ColorBufferFormat;
        graphics.PrimitiveTopology = PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP;
        graphics.DepthStencilDesc.DepthEnable = false;
        graphics.DepthStencilDesc.DepthWriteEnable = false;
        graphics.RasterizerDesc.CullMode = CULL_MODE_NONE;
        auto& blend = graphics.BlendDesc.RenderTargets[0];
        blend.BlendEnable = true;
        blend.SrcBlend = BLEND_FACTOR_ONE;
        blend.DestBlend = BLEND_FACTOR_INV_SRC_ALPHA;
        blend.SrcBlendAlpha = BLEND_FACTOR_ONE;
        blend.DestBlendAlpha = BLEND_FACTOR_INV_SRC_ALPHA;
        RefCntAutoPtr<IPipelineState> result;
        device->CreateGraphicsPipelineState(info, &result);
        if (result) {
            auto* variable = result->GetStaticVariableByName(SHADER_TYPE_VERTEX, "Parameters");
            if (!variable) {
                setError("DiligentCore could not bind compositor shader uniforms");
                return {};
            }
            variable->Set(uniforms);
        }
        return result;
    }

    std::unique_ptr<Texture> createImage(int width, int height, int format, bool pixelated)
    {
        auto texture = std::make_unique<Texture>(width, height, format);
        TextureDesc desc;
        desc.Name = "Podish compositor texture";
        desc.Type = RESOURCE_DIM_TEX_2D;
        desc.Width = width;
        desc.Height = height;
        desc.Format = format == 2 ? TEX_FORMAT_RGBA8_UNORM : TEX_FORMAT_BGRA8_UNORM;
        desc.BindFlags = BIND_SHADER_RESOURCE;
        desc.Usage = USAGE_DEFAULT;
        device->CreateTexture(desc, nullptr, &texture->image);
        if (!texture->image) {
            setError("DiligentCore could not create compositor texture");
            return {};
        }
        return bindImage(*texture, pixelated) ? std::move(texture) : nullptr;
    }

    bool bindImage(Texture& texture, bool pixelated)
    {
        imagePipeline->CreateShaderResourceBinding(&texture.bindings, true);
        auto* image = texture.bindings ? texture.bindings->GetVariableByName(SHADER_TYPE_PIXEL, "SourceTexture") : nullptr;
        auto* sampler = texture.bindings ? texture.bindings->GetVariableByName(SHADER_TYPE_PIXEL, "SourceSampler") : nullptr;
        if (!image || !sampler) {
            setError("DiligentCore could not bind compositor texture and sampler");
            return false;
        }
        image->Set(texture.image->GetDefaultView(TEXTURE_VIEW_SHADER_RESOURCE));
        sampler->Set(pixelated ? nearest : linear);
        return true;
    }

    bool create(void* metalLayer, int width, int height)
    {
        auto* factory = GetEngineFactoryVk();
        if (!factory) {
            setError("DiligentCore Vulkan factory unavailable");
            return false;
        }
        EngineVkCreateInfo config;
        const char* extensions[] = {VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME};
        config.DeviceExtensionCount = 1;
        config.ppDeviceExtensionNames = extensions;
        factory->CreateDeviceAndContextsVk(config, &device, &context);
        if (!device || !context) {
            setError("DiligentCore could not create Vulkan device");
            return false;
        }
        RefCntAutoPtr<IRenderDeviceVk> vulkan(device, IID_RenderDeviceVk);
        if (!vulkan) {
            setError("DiligentCore Vulkan device interface unavailable");
            return false;
        }
        vkDevice = vulkan->GetVkDevice();
        physicalDevice = vulkan->GetVkPhysicalDevice();
        VkPhysicalDeviceExternalMemoryHostPropertiesEXT hostProperties{
            VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_MEMORY_HOST_PROPERTIES_EXT};
        VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &hostProperties};
        vkGetPhysicalDeviceProperties2(physicalDevice, &properties);
        maxStorageBufferRange = properties.properties.limits.maxStorageBufferRange;
        hostPointerAlignment = hostProperties.minImportedHostPointerAlignment;
        if (!vkGetMemoryHostPointerPropertiesEXT || !hostPointerAlignment) {
            setError("Vulkan device cannot import shared host memory");
            return false;
        }

        VkPhysicalDeviceMemoryProperties memoryProperties{};
        vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memoryProperties);
        constexpr auto coherentHostMemory = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
        for (uint32_t i = 0; i < memoryProperties.memoryTypeCount; ++i) {
            if ((memoryProperties.memoryTypes[i].propertyFlags & coherentHostMemory) == coherentHostMemory)
                coherentMemoryTypes |= 1u << i;
        }

        NativeWindow window;
        window.pCALayer = metalLayer;
        SwapChainDesc swap;
        swap.Width = width;
        swap.Height = height;
        swap.ColorBufferFormat = TEX_FORMAT_BGRA8_UNORM;
        swap.DepthBufferFormat = TEX_FORMAT_UNKNOWN;
        factory->CreateSwapChainVk(device, context, swap, window, &swapChain);
        if (!swapChain) {
            setError("DiligentCore could not attach Vulkan to the compositor Metal layer");
            return false;
        }

        FenceDesc fenceDesc;
        fenceDesc.Name = "Podish compositor completed frames";
        fenceDesc.Type = FENCE_TYPE_CPU_WAIT_ONLY;
        device->CreateFence(fenceDesc, &fence);

        BufferDesc uniformDesc;
        uniformDesc.Name = "Podish compositor draw uniforms";
        uniformDesc.Size = sizeof(Parameters);
        uniformDesc.Usage = USAGE_DYNAMIC;
        uniformDesc.BindFlags = BIND_UNIFORM_BUFFER;
        uniformDesc.CPUAccessFlags = CPU_ACCESS_WRITE;
        device->CreateBuffer(uniformDesc, nullptr, &uniforms);
        if (!fence || !uniforms) {
            setError("DiligentCore could not create compositor fence or uniforms");
            return false;
        }

        SamplerDesc sampler;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = TEXTURE_ADDRESS_CLAMP;
        sampler.MinFilter = sampler.MagFilter = sampler.MipFilter = FILTER_TYPE_LINEAR;
        device->CreateSampler(sampler, &linear);
        sampler.MinFilter = sampler.MagFilter = sampler.MipFilter = FILTER_TYPE_POINT;
        device->CreateSampler(sampler, &nearest);
        if (!linear || !nearest) {
            setError("DiligentCore could not create compositor samplers");
            return false;
        }

        auto vertex = shader(SHADER_TYPE_VERTEX, "Podish compositor vertex", vertexShader, sizeof(vertexShader));
        auto image = shader(SHADER_TYPE_PIXEL, "Podish compositor image", imageShader, sizeof(imageShader));
        auto buffer = shader(SHADER_TYPE_PIXEL, "Podish compositor shared buffer", bufferShader, sizeof(bufferShader));
        if (!vertex || !image || !buffer) {
            setError("DiligentCore could not load compositor SPIR-V shaders");
            return false;
        }
        imagePipeline = pipeline(vertex, image, "Podish texture composition");
        bufferPipeline = pipeline(vertex, buffer, "Podish shared buffer composition");
        if (!imagePipeline || !bufferPipeline) {
            setError("DiligentCore could not create compositor Vulkan pipelines");
            return false;
        }
        emptyTexture = createImage(1, 1, 2, true);
        return bool(emptyTexture);
    }

    std::unique_ptr<Texture> import(int fd, uint64_t backingSize, uint64_t offset,
                                    int stride, int width, int height, int format)
    {
        if (offset > backingSize || stride <= 0 || stride % 4 != 0 || offset % 4 != 0 ||
            uint64_t(width) * 4 > uint64_t(stride)) {
            setError("Invalid PRIME buffer offset or stride");
            return {};
        }
        const uint64_t planeBytes = uint64_t(stride) * (height - 1) + uint64_t(width) * 4;
        if (planeBytes > backingSize - offset || offset + planeBytes > maxStorageBufferRange) {
            setError("PRIME pixels exceed allocation or Vulkan storage buffer limits");
            return {};
        }
        struct stat fileInfo{};
        if (fd < 0 || backingSize > SIZE_MAX || backingSize % hostPointerAlignment != 0 ||
            fstat(fd, &fileInfo) != 0 || fileInfo.st_size < 0 || uint64_t(fileInfo.st_size) < backingSize) {
            setError("Invalid PRIME shared allocation descriptor");
            return {};
        }

        auto texture = std::make_unique<Texture>(width, height, format);
        texture->vkDevice = vkDevice;
        texture->strideWords = uint32_t(stride / 4);
        texture->offsetWords = uint32_t(offset / 4);
        texture->mappingSize = size_t(backingSize);
        texture->mapping = mmap(nullptr, texture->mappingSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (texture->mapping == MAP_FAILED || reinterpret_cast<uintptr_t>(texture->mapping) % hostPointerAlignment != 0) {
            setError("Could not map aligned PRIME shared allocation");
            return {};
        }

        constexpr auto handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_MAPPED_FOREIGN_MEMORY_BIT_EXT;
        VkPhysicalDeviceExternalBufferInfo externalQuery{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_BUFFER_INFO};
        externalQuery.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
        externalQuery.handleType = handleType;
        VkExternalBufferProperties externalProperties{VK_STRUCTURE_TYPE_EXTERNAL_BUFFER_PROPERTIES};
        vkGetPhysicalDeviceExternalBufferProperties(physicalDevice, &externalQuery, &externalProperties);
        if (!(externalProperties.externalMemoryProperties.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT)) {
            setError("Vulkan device cannot import shared storage buffers");
            return {};
        }
        VkExternalMemoryBufferCreateInfo external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_BUFFER_CREATE_INFO};
        external.handleTypes = handleType;
        VkBufferCreateInfo desc{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, &external};
        desc.size = offset + planeBytes;
        desc.usage = externalQuery.usage;
        desc.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        VkResult result = vkCreateBuffer(vkDevice, &desc, nullptr, &texture->vkBuffer);
        if (result != VK_SUCCESS) {
            setVulkanError("Create external Vulkan buffer", result);
            return {};
        }

        VkMemoryRequirements requirements{};
        vkGetBufferMemoryRequirements(vkDevice, texture->vkBuffer, &requirements);
        VkMemoryHostPointerPropertiesEXT handleProperties{VK_STRUCTURE_TYPE_MEMORY_HOST_POINTER_PROPERTIES_EXT};
        result = vkGetMemoryHostPointerPropertiesEXT(vkDevice, handleType, texture->mapping, &handleProperties);
        if (result != VK_SUCCESS) {
            setVulkanError("Query shared Vulkan host memory", result);
            return {};
        }
        const uint32_t allowed = requirements.memoryTypeBits & handleProperties.memoryTypeBits & coherentMemoryTypes;
        if (!allowed || requirements.size > backingSize) {
            setError("Shared allocation length or memory type is incompatible with Vulkan");
            return {};
        }
        VkImportMemoryHostPointerInfoEXT importInfo{VK_STRUCTURE_TYPE_IMPORT_MEMORY_HOST_POINTER_INFO_EXT};
        importInfo.handleType = handleType;
        importInfo.pHostPointer = texture->mapping;
        VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, &importInfo};
        allocate.allocationSize = backingSize;
        allocate.memoryTypeIndex = __builtin_ctz(allowed);
        result = vkAllocateMemory(vkDevice, &allocate, nullptr, &texture->vkMemory);
        if (result != VK_SUCCESS) {
            setVulkanError("Import shared Vulkan host memory", result);
            return {};
        }
        result = vkBindBufferMemory(vkDevice, texture->vkBuffer, texture->vkMemory, 0);
        if (result != VK_SUCCESS) {
            setVulkanError("Bind external Vulkan buffer", result);
            return {};
        }

        BufferDesc buffer;
        buffer.Name = "Podish PRIME pixels";
        buffer.Size = desc.size;
        buffer.BindFlags = BIND_SHADER_RESOURCE;
        buffer.Usage = USAGE_DEFAULT;
        buffer.Mode = BUFFER_MODE_STRUCTURED;
        buffer.ElementByteStride = sizeof(uint32_t);
        RefCntAutoPtr<IRenderDeviceVk> vulkan(device, IID_RenderDeviceVk);
        vulkan->CreateBufferFromVulkanResource(texture->vkBuffer, buffer, RESOURCE_STATE_SHADER_RESOURCE,
                                              &texture->buffer);
        if (!texture->buffer) {
            setError("DiligentCore could not wrap the imported Vulkan buffer");
            return {};
        }
        bufferPipeline->CreateShaderResourceBinding(&texture->bindings, true);
        auto* pixels = texture->bindings ? texture->bindings->GetVariableByName(SHADER_TYPE_PIXEL, "GuestPixels") : nullptr;
        if (!pixels) {
            setError("DiligentCore could not bind the shared shader storage buffer");
            return {};
        }
        pixels->Set(texture->buffer->GetDefaultView(BUFFER_VIEW_SHADER_RESOURCE));
        return texture;
    }

    Texture* registerTexture(std::unique_ptr<Texture> texture)
    {
        if (!texture)
            return nullptr;
        textures.insert(texture.get());
        return texture.release();
    }
};

bool validTexture(int width, int height, int format)
{
    if (width <= 0 || height <= 0 || format < 0 || format > 2) {
        setError("Invalid compositor texture dimensions or format");
        return false;
    }
    return true;
}
} // namespace

const char *podish_compositor_last_error(void)
{
    return lastError;
}

void *podish_compositor_create(void* metalLayer, int width, int height)
{
    return protect<void*>(nullptr, [&]() -> void* {
        @autoreleasepool {
            if (!metalLayer || width <= 0 || height <= 0) {
                setError("Invalid compositor Metal layer or dimensions");
                return nullptr;
            }
            auto compositor = std::make_unique<Compositor>();
            if (!compositor->create(metalLayer, width, height))
                return nullptr;
            return compositor.release();
        }
    });
}

void podish_compositor_destroy(void* ctx)
{
    @autoreleasepool { delete static_cast<Compositor*>(ctx); }
}

int podish_compositor_resize(void* ctx, int width, int height)
{
    return protect(0, [&] {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            if (compositor.inFrame || width <= 0 || height <= 0) {
                setError("Invalid compositor resize request");
                return 0;
            }
            compositor.swapChain->Resize(width, height);
            return 1;
        }
    });
}

int podish_compositor_begin(void* ctx)
{
    return protect(0, [&] {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            if (compositor.inFrame)
                return 0;
            compositor.collect();
            ITextureView* target = compositor.swapChain->GetCurrentBackBufferRTV();
            if (!target) {
                setError("Compositor swapchain drawable unavailable");
                return 0;
            }
            auto& context = compositor.context;
            context->SetRenderTargets(1, &target, nullptr, RESOURCE_STATE_TRANSITION_MODE_TRANSITION);
            const float clear[] = {0.0f, 0.0f, 0.0f, 1.0f};
            context->ClearRenderTarget(target, clear, RESOURCE_STATE_TRANSITION_MODE_VERIFY);
            compositor.inFrame = true;
            return 1;
        }
    });
}

uint64_t podish_compositor_end(void* ctx)
{
    return protect<uint64_t>(0, [&] {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            if (!compositor.inFrame)
                return uint64_t(0);
            compositor.inFrame = false;
            const uint64_t serial = compositor.submitted + 1;
            compositor.context->EnqueueSignal(compositor.fence, serial);
            compositor.context->Flush();
            compositor.submitted = serial;
            protect(0, [&] {
                compositor.swapChain->Present(0);
                return 1;
            });
            return serial;
        }
    });
}

uint64_t podish_compositor_completed(void* ctx)
{
    return static_cast<Compositor*>(ctx)->collect();
}

void *podish_compositor_texture_create(void* ctx, int width, int height, int format, int nearest)
{
    return protect<void*>(nullptr, [&]() -> void* {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            if (!validTexture(width, height, format))
                return nullptr;
            return compositor.registerTexture(compositor.createImage(width, height, format, nearest != 0));
        }
    });
}

int podish_compositor_texture_update(void* ctx, void* handle, int x, int y, int width, int height,
                                     const void* pixels, int pitch)
{
    return protect(0, [&] {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            auto* texture = static_cast<Texture*>(handle);
            if (!pixels || !compositor.textures.count(texture) || !texture->image ||
                x < 0 || y < 0 || width <= 0 || height <= 0 || pitch <= 0 ||
                uint64_t(x) + width > texture->width || uint64_t(y) + height > texture->height ||
                uint64_t(width) * 4 > uint64_t(pitch)) {
                setError("Invalid compositor texture update bounds");
                return 0;
            }
            Box box(x, x + width, y, y + height);
            TextureSubResData source;
            source.pData = pixels;
            source.Stride = pitch;
            compositor.context->UpdateTexture(texture->image, 0, 0, box, source,
                                               RESOURCE_STATE_TRANSITION_MODE_NONE,
                                               RESOURCE_STATE_TRANSITION_MODE_TRANSITION);
            texture->lastUsed = compositor.submitted + 1;
            return 1;
        }
    });
}

void podish_compositor_texture_destroy(void* ctx, void* handle)
{
    protect(0, [&] {
        auto& compositor = *static_cast<Compositor*>(ctx);
        auto* texture = static_cast<Texture*>(handle);
        if (compositor.textures.count(texture)) {
            if (texture->lastUsed > compositor.collect())
                compositor.retired.emplace_back(texture);
            else
                delete texture;
            compositor.textures.erase(texture);
        }
        return 1;
    });
}

void *podish_compositor_texture_import_fd(void* ctx, int fd, uint64_t backingSize, uint64_t offset,
                                       int stride, int width, int height, int format)
{
    return protect<void*>(nullptr, [&]() -> void* {
        @autoreleasepool {
            auto& compositor = *static_cast<Compositor*>(ctx);
            if (!validTexture(width, height, format))
                return nullptr;
            return compositor.registerTexture(compositor.import(fd, backingSize, offset, stride, width, height, format));
        }
    });
}

void podish_compositor_draw(void* ctx, void* handle, const struct podish_compositor_draw* args)
{
    if (!args || args->rect[2] <= 0.0f || args->rect[3] <= 0.0f)
        return;
    auto& compositor = *static_cast<Compositor*>(ctx);
    if (!compositor.inFrame)
        return;
    auto* texture = handle ? static_cast<Texture*>(handle) : compositor.emptyTexture.get();
    if (handle && !compositor.textures.count(texture))
        return;

    const auto& swap = compositor.swapChain->GetDesc();
    Parameters parameters{};
    std::copy_n(args->rect, 4, parameters.rect);
    std::copy_n(args->color, 4, parameters.color);
    std::copy_n(args->title_color, 4, parameters.titleColor);
    std::copy_n(args->uv_origin, 2, parameters.originAndViewport);
    parameters.originAndViewport[2] = float(swap.Width);
    parameters.originAndViewport[3] = float(swap.Height);
    std::copy_n(args->uv_basis, 4, parameters.uvBasis);
    std::copy_n(args->params, 4, parameters.chrome);
    parameters.drawOptions[0] = args->mode;
    parameters.drawOptions[1] = float(texture->format);
    parameters.source[0] = texture->width;
    parameters.source[1] = texture->height;
    parameters.source[2] = texture->strideWords;
    parameters.source[3] = texture->offsetWords;

    auto& context = compositor.context;
    void* constants = nullptr;
    context->MapBuffer(compositor.uniforms, MAP_WRITE, MAP_FLAG_DISCARD, constants);
    if (!constants) {
        setError("Could not allocate compositor draw constants");
        return;
    }
    std::memcpy(constants, &parameters, sizeof(parameters));
    context->UnmapBuffer(compositor.uniforms, MAP_WRITE);

    context->SetPipelineState(texture->buffer ? compositor.bufferPipeline : compositor.imagePipeline);
    context->CommitShaderResources(texture->bindings, RESOURCE_STATE_TRANSITION_MODE_TRANSITION);
    DrawAttribs draw(4, DRAW_FLAG_NONE);
    context->Draw(draw);
    texture->lastUsed = compositor.submitted + 1;
}
