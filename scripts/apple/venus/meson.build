project('podish-venus', 'c', 'cpp', 'objc', 'objcpp',
  default_options: ['buildtype=release', 'default_library=static', 'b_ndebug=true'])

vulkan_inc = include_directories(get_option('vulkan_headers'))
kosmickrisp_library = get_option('kosmickrisp_library')
meson.override_dependency('vulkan', declare_dependency(include_directories: vulkan_inc))

host_zink = get_option('host_zink')
virgl_options = [
  'venus=true',
  'vulkan-dload=false',
  'render-server-mode=thread',
  'render-server-worker=thread',
]
if host_zink
  epoxy = subproject('libepoxy', default_options: [
    'default_library=static', 'egl=no', 'glx=no', 'x11=false', 'tests=false',
  ]).get_variable('libepoxy_dep')
  meson.override_dependency('epoxy', epoxy)
  virgl_options += ['default_library=static', 'vrend=true', 'platforms=[]']
else
  virgl_options += ['vrend=false']
endif
virgl = subproject('virglrenderer', default_options: virgl_options)

framework_names = ['CoreFoundation', 'CoreGraphics', 'Foundation', 'IOSurface', 'Metal', 'QuartzCore']
if get_option('apple_platform') == 'macosx'
  framework_names += ['AppKit', 'IOKit']
else
  framework_names += ['UIKit']
endif

sources = ['bridge.mm', 'renderer.c']
compositor_args = []
extra_link_args = []
extra_link_dependencies = []
native_libraries = [virgl.get_variable('libvirgl'), virgl.get_variable('libmesa')]
if host_zink
  sources += ['virgl.mm']
  compositor_args += ['-std=c++17']
  native_libraries += [virgl.get_variable('libvirglrenderer')]
  host_zink_exports = meson.current_source_dir() / 'virgl-exports.txt'
  extra_link_args += ['-Wl,-exported_symbols_list,' + host_zink_exports]
  extra_link_dependencies += [host_zink_exports]
endif
diligent_source = get_option('diligent_source')
diligent_archive = get_option('diligent_archive')
if diligent_archive != ''
  sources += ['compositor.mm']
  compositor_args += [
    '-std=c++17',
    '-DPLATFORM_APPLE=1',
    '-I' + diligent_source,
    '-I' + diligent_source / 'ThirdParty' / 'volk',
  ]
  if get_option('apple_platform') == 'macosx'
    compositor_args += ['-DPLATFORM_MACOS=1']
  else
    compositor_args += ['-DPLATFORM_IOS=1']
  endif
  extra_link_args += [diligent_archive]
  extra_link_dependencies += [diligent_archive]
endif

if get_option('apple_platform') == 'macosx'
  driver_rpath = '@loader_path/../../..'
else
  driver_rpath = '@loader_path/..'
endif

shared_library('PodishVenus',
  sources,
  objcpp_args: compositor_args,
  name_prefix: '',
  include_directories: [vulkan_inc, virgl.get_variable('libvirgl_inc')],
  dependencies: dependency('appleframeworks', modules: framework_names),
  link_with: native_libraries,
  link_args: [
    '-ObjC',
    '-Wl,-dead_strip',
    '-Wl,-headerpad_max_install_names',
    '-Wl,-rpath,' + driver_rpath,
    kosmickrisp_library,
    '-Wl,-exported_symbols_list,' + meson.current_source_dir() / 'exports.txt',
  ] + extra_link_args,
  link_depends: ['exports.txt', kosmickrisp_library] + extra_link_dependencies,
)
