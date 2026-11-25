#include "vulkan-dispatch.h"

#import <Foundation/Foundation.h>

#include <chrono>
#include <climits>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <fcntl.h>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <sys/mman.h>
#include <sys/uio.h>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <unistd.h>
#include <utility>

extern "C" {
#include "virgl_hw.h"
#include "vkr_renderer.h"

void podish_epoxy_set_gl_resolver(void *(*resolve)(const char *));
bool podish_virgl_init(const struct vkr_renderer_callbacks *callbacks);
}

namespace {

struct Staging {
    iovec view{};
    std::unordered_set<uint32_t> contexts;
    ~Staging()
    {
        if (view.iov_len)
            munmap(view.iov_base, view.iov_len);
    }

    int create(uint32_t size)
    {
        char name[32];
        snprintf(name, sizeof(name), "/podish-vg-%08x", arc4random());
        int fd = shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0600);
        if (fd < 0)
            return -1;
        shm_unlink(name);
        const uint64_t page = getpagesize();
        const uint64_t mappedSize = (uint64_t(size) + page - 1) & ~(page - 1);
        if (fcntl(fd, F_SETFD, FD_CLOEXEC) == 0 && ftruncate(fd, mappedSize) == 0) {
            void *address = mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
            if (address != MAP_FAILED) {
                view = {address, size};
                return fd;
            }
        }
        close(fd);
        return -1;
    }
};

struct HostGl {
    void *library = nullptr;
    int (*initialize)(void *) = nullptr;
    void (*shutdown)() = nullptr;
    void *(*create)(bool, int, int, bool) = nullptr;
    void (*destroy)(void *) = nullptr;
    int (*makeCurrent)(void *) = nullptr;
    int (*isGles)() = nullptr;
    void *(*resolve)(const char *) = nullptr;
    void *(*exportTexture)(uint32_t) = nullptr;

    ~HostGl()
    {
        if (library)
            dlclose(library);
    }

    template<typename T>
    T symbol(const char *name) const { return reinterpret_cast<T>(dlsym(library, name)); }

    bool open()
    {
        Dl_info info{};
        if (!dladdr(reinterpret_cast<const void *>(&podish_virgl_init), &info))
            return false;
        std::string path(info.dli_fname);
        const auto framework = path.rfind("/PodishVenus.framework/");
        if (framework == std::string::npos)
            return false;
        path.resize(framework);
        path += "/PodishHostZink.framework/PodishHostZink";
        library = dlopen(path.c_str(), RTLD_LOCAL | RTLD_NOW);
        if (!library)
            return false;

        initialize = symbol<decltype(initialize)>("podish_zink_initialize");
        shutdown = symbol<decltype(shutdown)>("podish_zink_shutdown");
        create = symbol<decltype(create)>("podish_zink_create_context");
        destroy = symbol<decltype(destroy)>("podish_zink_destroy_context");
        makeCurrent = symbol<decltype(makeCurrent)>("podish_zink_make_current");
        isGles = symbol<decltype(isGles)>("podish_zink_is_gles");
        resolve = symbol<decltype(resolve)>("podish_zink_get_proc_address");
        exportTexture = symbol<decltype(exportTexture)>("podish_zink_export_texture");
        return initialize && shutdown && create && destroy && makeCurrent && resolve;
    }
};

class Renderer {
public:
    explicit Renderer(const vkr_renderer_callbacks &callbacks)
        : retire(callbacks.retire_fence), thread(&Renderer::run, this) {}

    ~Renderer()
    {
        {
            std::lock_guard lock(mutex);
            stopping = true;
        }
        wake.notify_one();
        thread.join();
    }

    bool initialize()
    {
        return invoke([&] {
            if (!gl.open() || gl.initialize(reinterpret_cast<void *>(podish_vk_get_instance_proc_addr)) != 0)
                return false;
            podish_epoxy_set_gl_resolver(gl.resolve);
            callbacks.version = 3;
            callbacks.create_gl_context = [](void *cookie, int, virgl_renderer_gl_ctx_param *p) {
                return static_cast<Renderer *>(cookie)->gl.create(p->shared, p->major_ver, p->minor_ver,
                                                                  p->compat_ctx != 0);
            };
            callbacks.destroy_gl_context = [](void *cookie, void *ctx) {
                static_cast<Renderer *>(cookie)->gl.destroy(ctx);
            };
            callbacks.make_current = [](void *cookie, int, void *ctx) {
                return static_cast<Renderer *>(cookie)->gl.makeCurrent(ctx);
            };
            callbacks.write_context_fence = [](void *cookie, uint32_t context, uint32_t ring, uint64_t sequence) {
                auto &renderer = *static_cast<Renderer *>(cookie);
                const auto pending = renderer.fences.find(context);
                if (pending != renderer.fences.end() && --pending->second == 0)
                    renderer.fences.erase(pending);
                renderer.retire(context, ring, sequence);
            };
            int flags = gl.isGles && gl.isGles() ? VIRGL_RENDERER_USE_GLES : 0;
            initialized = virgl_renderer_init(this, flags, &callbacks) == 0;
            if (!initialized) {
                podish_epoxy_set_gl_resolver(nullptr);
                gl.shutdown();
            }
            return initialized;
        });
    }

    template<typename Function>
    auto invoke(Function function) -> decltype(function())
    {
        decltype(function()) result{};
        std::lock_guard caller(callers);
        std::unique_lock lock(mutex);
        work = [&] { result = function(); };
        wake.notify_one();
        done.wait(lock, [&] { return !work; });
        return result;
    }

    void destroyContext(uint32_t context)
    {
        virgl_renderer_context_destroy(context);
        fences.erase(context);
        for (auto &resource : staging)
            resource.second.contexts.erase(context);
    }

    bool submitFence(uint32_t context, uint64_t ring, uint64_t sequence)
    {
        // The VirGL command stream has one host GPU ring. Disable merge so every
        // guest sync_file receives its completion callback.
        if (ring != 0 || virgl_renderer_context_create_fence(context, 0, 0, sequence) != 0)
            return false;
        ++fences[context];
        return true;
    }

    bool createResource(uint32_t context, uint32_t resource, const uint32_t *data, int *outFd)
    {
        *outFd = -1;
        auto [at, added] = staging.try_emplace(resource);
        if (!added)
            return false;
        int fd = -1;
        if (data[12] != 0) {
            fd = at->second.create(data[12]);
            if (fd < 0) {
                staging.erase(at);
                return false;
            }
        }
        virgl_renderer_resource_create_args args{resource, data[0], data[1], data[2], data[3],
            data[4], data[5], data[6], data[7], data[8], data[9]};
        if (virgl_renderer_resource_create(&args, fd >= 0 ? &at->second.view : nullptr, fd >= 0 ? 1u : 0u) != 0) {
            if (fd >= 0)
                close(fd);
            staging.erase(at);
            return false;
        }
        virgl_renderer_ctx_attach_resource(context, resource);
        at->second.contexts.insert(context);
        *outFd = fd;
        return true;
    }

    void destroyResource(uint32_t context, uint32_t resource)
    {
        virgl_renderer_ctx_detach_resource(context, resource);
        virgl_renderer_resource_unref(resource);
        staging.erase(resource);
    }

    bool attachResource(uint32_t context, uint32_t resource)
    {
        const auto entry = staging.find(resource);
        if (entry == staging.end())
            return false;
        virgl_renderer_ctx_attach_resource(context, resource);
        entry->second.contexts.insert(context);
        return true;
    }

    bool detachResource(uint32_t context, uint32_t resource)
    {
        const auto entry = staging.find(resource);
        if (entry == staging.end() || !entry->second.contexts.erase(context))
            return false;
        virgl_renderer_ctx_detach_resource(context, resource);
        return true;
    }

    bool isAttached(uint32_t context, uint32_t resource) const
    {
        const auto entry = staging.find(resource);
        return entry != staging.end() && entry->second.contexts.count(context);
    }

    void *exportResource(uint32_t resource)
    {
        virgl_renderer_resource_info info{};
        if (!gl.exportTexture || virgl_renderer_resource_get_info(resource, &info) != 0 || !info.tex_id)
            return nullptr;
        return gl.exportTexture(info.tex_id);
    }

    bool canExport() const { return gl.exportTexture != nullptr; }

private:
    void run()
    {
        std::unique_lock lock(mutex);
        while (!stopping) {
            if (!work) {
                if (fences.empty())
                    wake.wait(lock);
                else
                    wake.wait_for(lock, std::chrono::milliseconds(1));
            }
            if (stopping)
                break;

            @autoreleasepool {
                if (work) {
                    work();
                    work = {};
                    done.notify_one();
                }
                if (!fences.empty())
                    virgl_renderer_poll();
            }
        }
        if (initialized) {
            virgl_renderer_cleanup(this);
            staging.clear();
            podish_epoxy_set_gl_resolver(nullptr);
            gl.shutdown();
        }
    }

    HostGl gl;
    virgl_renderer_callbacks callbacks{};
    vkr_renderer_retire_fence_callback_type retire;
    std::unordered_map<uint32_t, Staging> staging;
    std::unordered_map<uint32_t, uint32_t> fences;
    bool initialized = false;
    std::mutex callers;
    std::mutex mutex;
    std::condition_variable wake;
    std::condition_variable done;
    std::function<void()> work;
    bool stopping = false;
    std::thread thread;
};

std::mutex initialization;
std::unique_ptr<Renderer> renderer;

} // namespace

extern "C" bool podish_virgl_init(const vkr_renderer_callbacks *callbacks)
{
    if (!callbacks || !callbacks->retire_fence)
        return false;
    std::lock_guard lock(initialization);
    if (renderer)
        return true;
    auto candidate = std::make_unique<Renderer>(*callbacks);
    if (!candidate->initialize())
        return false;
    renderer = std::move(candidate);
    return true;
}

extern "C" bool podish_virgl_can_export(void)
{
    return renderer && renderer->canExport();
}

extern "C" uint32_t podish_virgl_get_caps(uint32_t capset, uint32_t version, void *data, uint32_t capacity)
{
    return renderer->invoke([&]() -> uint32_t {
        uint32_t maxVersion = 0;
        uint32_t size = 0;
        virgl_renderer_get_cap_set(capset, &maxVersion, &size);
        if (!size || version > maxVersion || (data && capacity < size))
            return 0;
        if (data) {
            virgl_renderer_fill_caps(capset, version, data);
            if (capset == 2) {
                // Legacy GEM staging is available for VirGL. RESOURCE_CREATE_BLOB
                // still belongs to the separate Venus context.
                auto *caps = static_cast<union virgl_caps *>(data);
                caps->v2.capability_bits &= ~VIRGL_CAP_ARB_BUFFER_STORAGE;
            }
        }
        return size;
    });
}

extern "C" bool podish_virgl_create_context(uint32_t context, uint32_t capset, uint32_t nameLength, const char *name)
{
    return renderer->invoke([&] {
        return virgl_renderer_context_create_with_flags(context, capset, nameLength, name) == 0;
    });
}

extern "C" void podish_virgl_destroy_context(uint32_t context)
{
    renderer->invoke([&] { renderer->destroyContext(context); return true; });
}

extern "C" bool podish_virgl_resource_create(uint32_t context, uint32_t resource, const uint32_t *data, int *outFd)
{
    if (!data || !outFd)
        return false;
    return renderer->invoke([&] { return renderer->createResource(context, resource, data, outFd); });
}

extern "C" void podish_virgl_resource_destroy(uint32_t context, uint32_t resource)
{
    renderer->invoke([&] { renderer->destroyResource(context, resource); return true; });
}

extern "C" bool podish_virgl_resource_attach(uint32_t context, uint32_t resource)
{
    return renderer->invoke([&] { return renderer->attachResource(context, resource); });
}

extern "C" void podish_virgl_resource_detach(uint32_t context, uint32_t resource)
{
    renderer->invoke([&] { return renderer->detachResource(context, resource); });
}

extern "C" bool podish_virgl_transfer(uint32_t context, uint32_t resource, bool toHost, const uint32_t *data)
{
    if (!data || data[7] > INT_MAX)
        return false;
    return renderer->invoke([&] {
        if (!renderer->isAttached(context, resource))
            return false;
        virgl_box box{data[1], data[2], data[3], data[4], data[5], data[6]};
        if (toHost)
            return virgl_renderer_transfer_write_iov(resource, context, int(data[7]), data[9], data[10], &box,
                                                      data[8], nullptr, 0) == 0;
        return virgl_renderer_transfer_read_iov(resource, context, data[7], data[9], data[10], &box,
                                                 data[8], nullptr, 0) == 0;
    });
}

extern "C" bool podish_virgl_submit_cmd(uint32_t context, const void *command, uint32_t bytes)
{
    if ((!command && bytes) || bytes % 4)
        return false;
    return renderer->invoke([&] {
        return virgl_renderer_submit_cmd(const_cast<void *>(command), context, int(bytes / 4)) == 0;
    });
}

extern "C" bool podish_virgl_submit_fence(uint32_t context, uint32_t flags, uint64_t ring, uint64_t sequence)
{
    if (flags & ~VIRGL_RENDERER_FENCE_FLAG_MERGEABLE)
        return false;
    return renderer->invoke([&] { return renderer->submitFence(context, ring, sequence); });
}

extern "C" void *podish_virgl_export_resource(uint32_t context, uint32_t resource)
{
    return renderer->invoke([&]() -> void * {
        return renderer->isAttached(context, resource) ? renderer->exportResource(resource) : nullptr;
    });
}
