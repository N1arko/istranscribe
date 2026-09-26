cmake_minimum_required(VERSION 3.22)
if(APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "14.2" CACHE STRING "Minimum supported macOS" FORCE)
endif()
project(istranscribe_speaker_runtime LANGUAGES C CXX)

# @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
# Build the reviewed C API without the unrelated eSpeak/Piper TTS dependency tree.
include(FetchContent)
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_TTS OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_PORTAUDIO OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_WEBSOCKET OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_BINARY OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_BUILD_C_API_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_PYTHON OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_GPU OFF CACHE BOOL "" FORCE)
set(SHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION ON CACHE BOOL "" FORCE)
set(SHERPA_ONNX_USE_PRE_INSTALLED_ONNXRUNTIME_IF_AVAILABLE OFF CACHE BOOL "" FORCE)
add_compile_definitions(EIGEN_MPL2_ONLY)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5 CACHE STRING "" FORCE)
FetchContent_Declare(speaker_upstream
    URL https://codeload.github.com/k2-fsa/sherpa-onnx/tar.gz/26aa2fa93210376a89de3a65a1a4dd320c37f5e9
    URL_HASH SHA256=7c2daea812195ebef5f0799e68a907319e732b152176b0efa4e1dd7660df6572
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(speaker_upstream)
if(APPLE)
    set_target_properties(sherpa-onnx-c-api PROPERTIES
        BUILD_WITH_INSTALL_RPATH TRUE
        INSTALL_RPATH "@loader_path")
endif()
