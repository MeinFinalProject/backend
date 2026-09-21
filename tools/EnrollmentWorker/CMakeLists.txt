cmake_minimum_required(VERSION 3.24)
project(EnrollmentWorker LANGUAGES CXX)
if(NOT WIN32 OR NOT DEFINED EDGE_SOURCE_DIR)
  message(FATAL_ERROR "Requires Windows and -DEDGE_SOURCE_DIR=<edge-client checkout>")
endif()
set(VISION "${EDGE_SOURCE_DIR}/vision_runtime")
add_executable(enrollment_worker main.cpp
  "${VISION}/scrfd/detector.cpp" "${VISION}/scrfd/gpu_preprocessor.cpp" "${VISION}/scrfd/scrfd.cpp"
  "${VISION}/arcface/gpu_preprocessor.cpp" "${VISION}/arcface/arcface.cpp")
target_include_directories(enrollment_worker PRIVATE "${VISION}")
target_compile_features(enrollment_worker PRIVATE cxx_std_20)
target_compile_definitions(enrollment_worker PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(enrollment_worker PRIVATE /EHsc /W4)
target_link_libraries(enrollment_worker PRIVATE windowsapp windowscodecs d3d11 d3d12 dxgi d3dcompiler ole32)
