# The weft runtime as one native plugin for Unity: weft's static libraries (its View, scene and DOM, QuickJS, Yoga,
# FreeType, HarfBuzz) behind the C ABI of include/weft, and the plugin's own C functions (weft_unity_*: the owner
# thread the pages run on, the page I/O, and Unity's rendering events on D3D11, D3D12 and Vulkan). The DLL exports
# those and Unity's UnityPluginLoad/UnityPluginUnload, nothing else.
#
#   cmake -S Native~ -B <build> -DWEFT_SOURCE_DIR=<weft checkout> -DUNITY_PLUGIN_API_DIR=<Unity>/Editor/Data/PluginAPI
#   cmake --build <build> --config Release
#
# Scripts~/BuildWeft.ps1 does both and copies the DLL into Runtime/Plugins/x86_64.
cmake_minimum_required(VERSION 3.25)
project(WeftUnity LANGUAGES C CXX)

set(WEFT_SOURCE_DIR "" CACHE PATH "The weft checkout to build")
set(UNITY_PLUGIN_API_DIR "" CACHE PATH "Unity's native plugin headers (<Unity>/Editor/Data/PluginAPI)")
if(NOT EXISTS "${WEFT_SOURCE_DIR}/include/weft/weft.h")
    message(FATAL_ERROR "WEFT_SOURCE_DIR (${WEFT_SOURCE_DIR}) is not a weft checkout")
endif()
if(NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphics.h")
    message(FATAL_ERROR "UNITY_PLUGIN_API_DIR (${UNITY_PLUGIN_API_DIR}) holds no IUnityGraphics.h")
endif()

# Weft's libraries only: no tests, probes or samples.
set(WEFT_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WEFT_BUILD_PROBES OFF CACHE BOOL "" FORCE)
set(WEFT_BUILD_SAMPLES OFF CACHE BOOL "" FORCE)
add_subdirectory("${WEFT_SOURCE_DIR}" weft)

# The commit the DLL is built from, which the plugin logs when it starts ("+changes" for a checkout with changes).
execute_process(COMMAND git -C "${WEFT_SOURCE_DIR}" rev-parse HEAD OUTPUT_VARIABLE weft_commit
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
execute_process(COMMAND git -C "${WEFT_SOURCE_DIR}" status --porcelain --untracked-files=no OUTPUT_VARIABLE weft_dirty
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
if(weft_dirty)
    string(APPEND weft_commit "+changes")
endif()

# Every function weft's public headers declare is exported by name (a module definition file written from the
# headers), with the plugin's own and Unity's entry points.
set(weft_headers weft.h render_backend.h render_external_texture.h render_backend_d3d11.h render_backend_d3d12.h)
set(weft_backends weft::scene_d3d11 weft::scene_d3d12)
if(TARGET weft::scene_vulkan)
    list(APPEND weft_backends weft::scene_vulkan)
    list(APPEND weft_headers render_backend_vulkan.h)
endif()
set(export_sources "${CMAKE_CURRENT_SOURCE_DIR}/include/weft_unity.h")
foreach(header ${weft_headers})
    list(APPEND export_sources "${WEFT_SOURCE_DIR}/include/weft/${header}")
endforeach()
set(exports "")
foreach(path ${export_sources})
    file(READ "${path}" text)
    string(REGEX MATCHALL "[ *]weft_[a-z0-9_]+\\(" names "${text}")
    foreach(name ${names})
        string(REGEX REPLACE "^[ *]|\\($" "" name "${name}")
        list(APPEND exports "${name}")
    endforeach()
endforeach()
list(APPEND exports UnityPluginLoad UnityPluginUnload)
list(REMOVE_DUPLICATES exports)
list(SORT exports)
list(JOIN exports "\n    " export_lines)
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/weft.def" "LIBRARY weft\nEXPORTS\n    ${export_lines}\n")

add_library(weft_unity SHARED
    src/owner_thread.cpp
    src/message_pack.cpp
    src/page_host.cpp
    src/draw_queue.cpp
    src/render_device.cpp
    src/render_d3d11.cpp
    src/render_d3d12.cpp
    src/unity_plugin.cpp
    "${CMAKE_CURRENT_BINARY_DIR}/weft.def")
set_target_properties(weft_unity PROPERTIES OUTPUT_NAME weft)
target_compile_features(weft_unity PRIVATE cxx_std_20)
target_include_directories(weft_unity PRIVATE include src "${UNITY_PLUGIN_API_DIR}")
target_compile_definitions(weft_unity PRIVATE WEFT_UNITY_COMMIT="${weft_commit}" NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(weft_unity PRIVATE weft::core weft::view ${weft_backends} d3d11 d3d12 dxgi)
if(TARGET weft::scene_vulkan)
    target_sources(weft_unity PRIVATE src/render_vulkan.cpp)
    target_compile_definitions(weft_unity PRIVATE WEFT_UNITY_VULKAN=1)
endif()
if(MSVC)
    target_compile_options(weft_unity PRIVATE /W4 /utf-8)
    target_link_options(weft_unity PRIVATE /DEBUG /OPT:REF /OPT:ICF)
endif()

# The plugin's native contract outside Unity (tests/plugin_tests.cpp): stand-in Unity interfaces over a D3D11 or D3D12
# device.
option(WEFT_UNITY_TESTS "Build the plugin's native tests" ON)
if(WEFT_UNITY_TESTS)
    add_executable(weft_unity_tests tests/plugin_tests.cpp)
    target_compile_features(weft_unity_tests PRIVATE cxx_std_20)
    target_include_directories(weft_unity_tests PRIVATE include "${WEFT_SOURCE_DIR}/include" "${UNITY_PLUGIN_API_DIR}")
    target_compile_definitions(weft_unity_tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(weft_unity_tests PRIVATE weft_unity d3d11 d3d12 dxgi)
    if(MSVC)
        target_compile_options(weft_unity_tests PRIVATE /W4 /utf-8)
    endif()
endif()
