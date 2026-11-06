#include <algorithm>
#include <cmath>
#include <memory>
#include "api/echo_canceller3_factory.h"
#include "api/environment.h"
#include "audio_processing/audio_buffer.h"
#include "audio_processing/resampler/push_sinc_resampler.h"
#include "rnnoise.h"

namespace {
constexpr int kRate = 48000;
constexpr int kFrame = 480;
// Converts one 10 ms block from input_rate to output_rate.
struct Resampler {
  int input_frames;
  int output_frames;
  webrtc::PushSincResampler filter;
  Resampler(int input_rate, int output_rate)
      : input_frames(input_rate / 100), output_frames(output_rate / 100),
        filter(input_frames, output_frames) {}
};
bool ValidRate(int rate) { return rate >= 8000 && rate <= 192000 && rate % 100 == 0; }
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
API int aec3_abi_version() noexcept { return 3; }
// Resamples to 48 kHz (input_rate / 100 samples in, 480 out).
API void* aec3_resampler_create(int rate) noexcept {
  if (!ValidRate(rate)) return nullptr;
  try { return new Resampler(rate, kRate); }
  catch (...) { return nullptr; }
}
// Any pair of rates (input_rate / 100 samples in, output_rate / 100 out). ABI version 3.
API void* aec3_resampler_create_pair(int input_rate, int output_rate) noexcept {
  if (!ValidRate(input_rate) || !ValidRate(output_rate)) return nullptr;
  try { return new Resampler(input_rate, output_rate); }
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
    return static_cast<int>(resampler.filter.Resample(input, count, output, resampler.output_frames));
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
// RNNoise noise suppression on 10 ms frames at 48 kHz (its native rate and frame size).
// Samples are normalized floats, processed in place. All calls on an instance must be
// serialized by the caller. Returns the voice-activity probability, or a negative error.
API void* ns_create() noexcept {
  if (rnnoise_get_frame_size() != kFrame) return nullptr;
  return rnnoise_create(nullptr);
}
API void ns_destroy(void* instance) noexcept {
  if (instance) rnnoise_destroy(static_cast<DenoiseState*>(instance));
}
API float ns_process(void* instance, float* samples, int count) noexcept {
  if (!instance || !samples || count != kFrame) return -1;
  float frame[kFrame];
  for (int i = 0; i < kFrame; ++i)
    frame[i] = std::isfinite(samples[i]) ? std::clamp(samples[i], -1.0f, 1.0f) * 32768.0f : 0.0f;
  float vad = rnnoise_process_frame(static_cast<DenoiseState*>(instance), frame, frame);
  for (int i = 0; i < kFrame; ++i)
    samples[i] = std::clamp(frame[i] / 32768.0f, -1.0f, 1.0f);
  return vad;
}
