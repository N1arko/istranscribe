cmake_minimum_required(VERSION 3.24)

cmake_policy(SET CMP0091 NEW)
project(istranscribe_whisper_v1 VERSION 1.0.0 LANGUAGES C CXX)

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration

set(ITW_WHISPER_SOURCE_DIR "" CACHE PATH "Verified whisper.cpp source root")
set(ITW_TARGET_RID "" CACHE STRING "Target runtime identifier")
set(ITW_BACKEND "" CACHE STRING "Native runtime variant")
set(ITW_OUTPUT_DIRECTORY "" CACHE PATH "Non-packaged native build output")

if(NOT IS_ABSOLUTE "${ITW_WHISPER_SOURCE_DIR}" OR
   NOT EXISTS "${ITW_WHISPER_SOURCE_DIR}/include/whisper.h" OR
   NOT EXISTS "${ITW_WHISPER_SOURCE_DIR}/ggml/include/ggml-backend.h" OR
   NOT EXISTS "${ITW_WHISPER_SOURCE_DIR}/LICENSE")
    message(FATAL_ERROR "ITW_WHISPER_SOURCE_DIR must be an absolute verified whisper.cpp source root")
endif()

if(NOT IS_ABSOLUTE "${ITW_OUTPUT_DIRECTORY}")
    message(FATAL_ERROR "ITW_OUTPUT_DIRECTORY must be absolute")
endif()

file(REAL_PATH "${ITW_WHISPER_SOURCE_DIR}" ITW_WHISPER_SOURCE_REAL)
file(REAL_PATH "${CMAKE_CURRENT_SOURCE_DIR}" ITW_BRIDGE_SOURCE_REAL)
file(REAL_PATH "${CMAKE_BINARY_DIR}" ITW_BUILD_REAL)
cmake_path(IS_PREFIX ITW_WHISPER_SOURCE_REAL "${ITW_BUILD_REAL}" NORMALIZE ITW_BUILD_IN_UPSTREAM)
cmake_path(IS_PREFIX ITW_BUILD_REAL "${ITW_WHISPER_SOURCE_REAL}" NORMALIZE ITW_UPSTREAM_IN_BUILD)
cmake_path(IS_PREFIX ITW_BRIDGE_SOURCE_REAL "${ITW_BUILD_REAL}" NORMALIZE ITW_BUILD_IN_BRIDGE)
cmake_path(IS_PREFIX ITW_BUILD_REAL "${ITW_BRIDGE_SOURCE_REAL}" NORMALIZE ITW_BRIDGE_IN_BUILD)
if(ITW_BUILD_IN_UPSTREAM OR ITW_UPSTREAM_IN_BUILD OR
   ITW_BUILD_IN_BRIDGE OR ITW_BRIDGE_IN_BUILD)
    message(FATAL_ERROR "Native source, build, and bridge roots must not overlap")
endif()

# Keep app binaries reproducible across isolated source/build roots and avoid
# embedding developer paths through upstream __FILE__ diagnostics.
if(MSVC)
    add_compile_options(
        "/experimental:deterministic"
        "/pathmap:${ITW_WHISPER_SOURCE_REAL}=upstream/whisper.cpp"
        "/pathmap:${ITW_BRIDGE_SOURCE_REAL}=native/whisper"
        "/pathmap:${ITW_BUILD_REAL}=build")
else()
    add_compile_options(
        "-ffile-prefix-map=${ITW_WHISPER_SOURCE_REAL}=upstream/whisper.cpp"
        "-ffile-prefix-map=${ITW_BRIDGE_SOURCE_REAL}=native/whisper"
        "-ffile-prefix-map=${ITW_BUILD_REAL}=build")
endif()

set(ITW_ACCELERATOR 0)
set(ITW_OUTPUT_NAME "istranscribe_whisper_v1")

if(ITW_TARGET_RID STREQUAL "osx-arm64")
    if(NOT APPLE OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
        message(FATAL_ERROR "osx-arm64 must be built by a 64-bit Apple toolchain")
    endif()
    if(NOT ITW_BACKEND STREQUAL "metal")
        message(FATAL_ERROR "osx-arm64 supports the metal runtime variant only")
    endif()
    if(NOT CMAKE_OSX_ARCHITECTURES STREQUAL "arm64")
        message(FATAL_ERROR "osx-arm64 requires exact CMAKE_OSX_ARCHITECTURES=arm64")
    endif()
    if(NOT CMAKE_OSX_DEPLOYMENT_TARGET STREQUAL "14.2")
        message(FATAL_ERROR "osx-arm64 requires exact CMAKE_OSX_DEPLOYMENT_TARGET=14.2")
    endif()
    set(ITW_ACCELERATOR 2)
elseif(ITW_TARGET_RID STREQUAL "win-x64")
    if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
        message(FATAL_ERROR "win-x64 must be built by a 64-bit Windows toolchain")
    endif()
    if(ITW_BACKEND STREQUAL "cpu")
        set(ITW_ACCELERATOR 0)
    elseif(ITW_BACKEND STREQUAL "vulkan")
        set(ITW_ACCELERATOR 3)
        set(ITW_OUTPUT_NAME "istranscribe_whisper_v1_vulkan")
    else()
        message(FATAL_ERROR "win-x64 supports cpu and vulkan runtime variants")
    endif()
else()
    message(FATAL_ERROR "ITW_TARGET_RID must be osx-arm64 or win-x64")
endif()

# Upstream remains a static implementation detail. CLI, server, downloader,
# Python helpers and optional out-of-scope accelerators are absent from the
# product native payload.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(WHISPER_CURL OFF CACHE BOOL "" FORCE)
set(WHISPER_SDL2 OFF CACHE BOOL "" FORCE)
set(WHISPER_COMMON_FFMPEG OFF CACHE BOOL "" FORCE)
set(WHISPER_COREML OFF CACHE BOOL "" FORCE)
set(WHISPER_OPENVINO OFF CACHE BOOL "" FORCE)
set(WHISPER_USE_SYSTEM_GGML OFF CACHE BOOL "" FORCE)
set(WHISPER_ALL_WARNINGS OFF CACHE BOOL "" FORCE)

set(GGML_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GGML_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_LTO OFF CACHE BOOL "" FORCE)
set(GGML_CCACHE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL OFF CACHE BOOL "" FORCE)
set(GGML_RPC OFF CACHE BOOL "" FORCE)
set(GGML_CUDA OFF CACHE BOOL "" FORCE)
set(GGML_HIP OFF CACHE BOOL "" FORCE)
set(GGML_MUSA OFF CACHE BOOL "" FORCE)
set(GGML_SYCL OFF CACHE BOOL "" FORCE)
set(GGML_OPENCL OFF CACHE BOOL "" FORCE)
set(GGML_OPENVINO OFF CACHE BOOL "" FORCE)
set(GGML_WEBGPU OFF CACHE BOOL "" FORCE)
set(GGML_KOMPUTE OFF CACHE BOOL "" FORCE)

if(ITW_TARGET_RID STREQUAL "osx-arm64")
    set(GGML_METAL ON CACHE BOOL "" FORCE)
    set(GGML_METAL_EMBED_LIBRARY ON CACHE BOOL "" FORCE)
    set(GGML_METAL_NDEBUG ON CACHE BOOL "" FORCE)
    set(GGML_METAL_MACOSX_VERSION_MIN "14.2" CACHE STRING "" FORCE)
    set(GGML_ACCELERATE ON CACHE BOOL "" FORCE)
    set(GGML_BLAS ON CACHE BOOL "" FORCE)
    set(GGML_BLAS_VENDOR "Apple" CACHE STRING "" FORCE)
    set(GGML_VULKAN OFF CACHE BOOL "" FORCE)
else()
    set(GGML_METAL OFF CACHE BOOL "" FORCE)
    set(GGML_METAL_EMBED_LIBRARY OFF CACHE BOOL "" FORCE)
    set(GGML_ACCELERATE OFF CACHE BOOL "" FORCE)
    set(GGML_BLAS OFF CACHE BOOL "" FORCE)
    if(ITW_BACKEND STREQUAL "vulkan")
        set(GGML_VULKAN ON CACHE BOOL "" FORCE)
    else()
        set(GGML_VULKAN OFF CACHE BOOL "" FORCE)
    endif()

    # Portable Windows baseline: no host-specific compiler tuning and no
    # mandatory post-SSE2 instruction set.
    set(GGML_SSE42 OFF CACHE BOOL "" FORCE)
    set(GGML_AVX OFF CACHE BOOL "" FORCE)
    set(GGML_AVX_VNNI OFF CACHE BOOL "" FORCE)
    set(GGML_AVX2 OFF CACHE BOOL "" FORCE)
    set(GGML_BMI2 OFF CACHE BOOL "" FORCE)
    set(GGML_AVX512 OFF CACHE BOOL "" FORCE)
    set(GGML_FMA OFF CACHE BOOL "" FORCE)
    set(GGML_F16C OFF CACHE BOOL "" FORCE)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_subdirectory("${ITW_WHISPER_SOURCE_REAL}" upstream-whisper EXCLUDE_FROM_ALL)
if(NOT TARGET whisper)
    message(FATAL_ERROR "Pinned source did not define the whisper target")
endif()

add_library(istranscribe_whisper_v1 SHARED
    include/istranscribe_whisper_v1.h
    src/istranscribe_whisper_v1.cpp)

target_compile_features(istranscribe_whisper_v1 PRIVATE cxx_std_17)
target_include_directories(istranscribe_whisper_v1
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(istranscribe_whisper_v1 PRIVATE
    ISTRANSCRIBE_WHISPER_BUILD=1
    ISTRANSCRIBE_WHISPER_ACCELERATOR=${ITW_ACCELERATOR})
target_link_libraries(istranscribe_whisper_v1 PRIVATE whisper)

set_target_properties(istranscribe_whisper_v1 PROPERTIES
    OUTPUT_NAME "${ITW_OUTPUT_NAME}"
    PREFIX ""
    CXX_VISIBILITY_PRESET hidden
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    RUNTIME_OUTPUT_DIRECTORY "${ITW_OUTPUT_DIRECTORY}"
    LIBRARY_OUTPUT_DIRECTORY "${ITW_OUTPUT_DIRECTORY}"
    ARCHIVE_OUTPUT_DIRECTORY "${ITW_OUTPUT_DIRECTORY}")

if(APPLE)
    set_target_properties(istranscribe_whisper_v1 PROPERTIES
        INSTALL_NAME_DIR "@rpath"
        BUILD_WITH_INSTALL_NAME_DIR YES)
endif()

foreach(ITW_CONFIGURATION Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER "${ITW_CONFIGURATION}" ITW_CONFIGURATION_UPPER)
    set_target_properties(istranscribe_whisper_v1 PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${ITW_CONFIGURATION_UPPER} "${ITW_OUTPUT_DIRECTORY}"
        LIBRARY_OUTPUT_DIRECTORY_${ITW_CONFIGURATION_UPPER} "${ITW_OUTPUT_DIRECTORY}"
        ARCHIVE_OUTPUT_DIRECTORY_${ITW_CONFIGURATION_UPPER} "${ITW_OUTPUT_DIRECTORY}")
endforeach()

if(MSVC)
    target_sources(istranscribe_whisper_v1 PRIVATE exports/windows.def)
    target_compile_options(istranscribe_whisper_v1 PRIVATE /W4 /WX /permissive- /utf-8)
else()
    target_compile_options(istranscribe_whisper_v1 PRIVATE
        -Wall -Wextra -Wpedantic -Werror)
endif()

if(APPLE)
    target_link_options(istranscribe_whisper_v1 PRIVATE
        "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports/macos.exports")
endif()
