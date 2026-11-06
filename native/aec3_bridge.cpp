#include <algorithm>
#include <cmath>
#include <memory>
#include "api/echo_canceller3_factory.h"
#include "api/environment.h"
#include "audio_processing/audio_buffer.h"
#include "audio_processing/resampler/push_sinc_resampler.h"

namespace {
constexpr int kRate = 48000;
constexpr int kFrame = 480;
struct Resampler {
  int input_frames;
  webrtc::PushSincResampler filter;
  explicit Resampler(int rate) : input_frames(rate / 100), filter(input_frames, kFrame) {}
};
struct Processor {
  webrtc::Environment environment;
  std::unique_ptr<webrtc::EchoControl> echo;
  webrtc::AudioBuffer render{kRate, 1, kRate, 1, kRate, 1};
  webrtc::AudioBuffer capture{kRate, 1, kRate, 1, kRate, 1};
  Processor() {
    webrtc::EchoCanceller3Config config;
    echo = webrtc::EchoCanceller3Factory(config).Create(environment, kRate, 1, 1);
  }
};
void Fill(webrtc::AudioBuffer& target, const float* source) {
  for (int i = 0; i < kFrame; ++i)
    target.channels()[0][i] = std::isfinite(source[i])
        ? std::clamp(source[i], -1.0f, 1.0f) * 32768.0f : 0.0f;
}
}
#define API extern "C" __declspec(dllexport)
API int aec3_abi_version() noexcept { return 1; }
API void* aec3_resampler_create(int rate) noexcept {
  if (rate < 8000 || rate > 192000 || rate % 100) return nullptr;
  try { return new Resampler(rate); }
  catch (...) { return nullptr; }
}
API void aec3_resampler_destroy(void* instance) noexcept {
  delete static_cast<Resampler*>(instance);
}
API int aec3_resample(void* instance, const float* input, int count, float* output) noexcept {
  if (!instance || !input || !output) return -1;
  auto& resampler = *static_cast<Resampler*>(instance);
  if (count != resampler.input_frames) return -1;
  try {
    return static_cast<int>(resampler.filter.Resample(input, count, output, kFrame));
  } catch (...) { return -2; }
}
API void* aec3_create() noexcept {
  try { return new Processor(); } catch (...) { return nullptr; }
}
API void aec3_destroy(void* instance) noexcept { delete static_cast<Processor*>(instance); }
// All calls on an instance must be serialized by the caller. Samples are normalized floats.
API int aec3_render(void* instance, const float* samples, int count) noexcept {
  if (!instance || !samples || count != kFrame) return -1;
  try {
    auto& p = *static_cast<Processor*>(instance);
    Fill(p.render, samples);
    p.render.SplitIntoFrequencyBands();
    p.echo->AnalyzeRender(&p.render);
    return 0;
  } catch (...) { return -2; }
}
API int aec3_capture(void* instance, float* samples, int count) noexcept {
  if (!instance || !samples || count != kFrame) return -1;
  try {
    auto& p = *static_cast<Processor*>(instance);
    Fill(p.capture, samples);
    p.echo->AnalyzeCapture(&p.capture);
    p.capture.SplitIntoFrequencyBands();
    p.echo->ProcessCapture(&p.capture, false);
    p.capture.MergeFrequencyBands();
    for (int i = 0; i < kFrame; ++i)
      samples[i] = std::clamp(p.capture.channels_const()[0][i] / 32768.0f, -1.0f, 1.0f);
    return 0;
  } catch (...) { return -2; }
}
