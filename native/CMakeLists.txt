cmake_minimum_required(VERSION 3.24)
project(Aec3Native LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Windows x64 is required")
endif()
include(FetchContent)
FetchContent_Declare(webrtc
  GIT_REPOSITORY https://github.com/Enaium/webrtc-aec3.git
  GIT_TAG 2cec2f52e26646f93bd2d5498bbabf59cba18da9
  SOURCE_SUBDIR unused)
# RNNoise 0.2 release tarball: unlike the git tree it includes the trained model (rnnoise_data.c).
FetchContent_Declare(rnnoise
  URL https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz
  URL_HASH SHA256=90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37
  SOURCE_SUBDIR unused)
FetchContent_MakeAvailable(webrtc rnnoise)
set(RNNOISE_ROOT "${rnnoise_SOURCE_DIR}")
# SSE2 build, the x64 baseline (no runtime CPU dispatch); the per-frame cost is small enough.
# MSVC does not define __SSE2__, and the tarball's plain C path lacks os_support.h.
add_library(rnnoise STATIC
  "${RNNOISE_ROOT}/src/denoise.c" "${RNNOISE_ROOT}/src/rnn.c" "${RNNOISE_ROOT}/src/pitch.c"
  "${RNNOISE_ROOT}/src/kiss_fft.c" "${RNNOISE_ROOT}/src/celt_lpc.c" "${RNNOISE_ROOT}/src/nnet.c"
  "${RNNOISE_ROOT}/src/nnet_default.c" "${RNNOISE_ROOT}/src/parse_lpcnet_weights.c"
  "${RNNOISE_ROOT}/src/rnnoise_data.c" "${RNNOISE_ROOT}/src/rnnoise_tables.c")
target_include_directories(rnnoise PUBLIC "${RNNOISE_ROOT}/include" PRIVATE "${RNNOISE_ROOT}/src")
target_compile_definitions(rnnoise PRIVATE __SSE2__ RNNOISE_BUILD _CRT_SECURE_NO_WARNINGS _USE_MATH_DEFINES)
if(MSVC)
  target_compile_options(rnnoise PRIVATE /W0 /utf-8)
endif()
set(ROOT "${webrtc_SOURCE_DIR}/src")
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS "${ROOT}/*.cc" "${ROOT}/*.c" "${ROOT}/*.cpp")
list(FILTER SOURCES EXCLUDE REGEX ".*_neon\\.cc$")
if(MSVC)
  # GCC vector subscripts are not supported by MSVC; use its intrinsic union fields.
  set(MATCHED_FILTER "${ROOT}/audio_processing/aec3/matched_filter_avx2.cc")
  file(READ "${MATCHED_FILTER}" MATCHED_TEXT)
  string(REPLACE "s_inst_hadd_256[" "s_inst_hadd_256.m256_f32[" MATCHED_TEXT "${MATCHED_TEXT}")
  string(REPLACE "e_128[" "e_128.m128_f32[" MATCHED_TEXT "${MATCHED_TEXT}")
  string(REPLACE "sum[" "sum.m128_f32[" MATCHED_TEXT "${MATCHED_TEXT}")
  file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/matched_filter_avx2.cc" CONTENT "${MATCHED_TEXT}" @ONLY)
  list(REMOVE_ITEM SOURCES "${MATCHED_FILTER}")
  list(APPEND SOURCES "${CMAKE_CURRENT_BINARY_DIR}/matched_filter_avx2.cc")
endif()
add_library(Aec3Native SHARED aec3_bridge.cpp ${SOURCES})
target_compile_definitions(Aec3Native PRIVATE WEBRTC_WIN NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
target_include_directories(Aec3Native PRIVATE
  "${ROOT}" "${ROOT}/api" "${ROOT}/base" "${ROOT}/base/rtc_base"
  "${ROOT}/base/system_wrappers/include" "${ROOT}/base/jsoncpp/include"
  "${ROOT}/absl" "${ROOT}/audio_processing" "${ROOT}/audio_processing/aec3"
  "${ROOT}/audio_processing/resampler" "${ROOT}/audio_processing/logging"
  "${ROOT}/audio_processing/include" "${ROOT}/audio_processing/utility"
  "${ROOT}/rtc_base/experiments" "${ROOT}/common_audio"
  "${ROOT}/common_audio/third_party/ooura/fft_size_128")
if(MSVC)
  # The extracted audio_buffer.cc uses std::array without including its header.
  set_source_files_properties("${ROOT}/audio_processing/audio_buffer.cc" PROPERTIES COMPILE_OPTIONS /FIarray)
  target_compile_options(Aec3Native PRIVATE /W3 /EHsc /utf-8)
  # No /arch:AVX2 for the *_avx2.cc files: MSVC compiles their intrinsics without it, and
  # with it the inline functions they share with the rest (std::fill, std::span, ...) could
  # be linked in their AVX2 build and crash CPUs without AVX2. The extracted
  # cpu_info::Supports always returns false, so these AVX2 paths are never called anyway.
endif()
target_link_libraries(Aec3Native PRIVATE winmm rnnoise)
set_target_properties(Aec3Native PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/out/$<CONFIG>")
