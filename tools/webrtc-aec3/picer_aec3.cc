#include "picer_aec3.h"

#include <algorithm>
#include <vector>

#include "api/audio/audio_processing.h"
#include "api/audio/builtin_audio_processing_builder.h"
#include "api/environment/environment_factory.h"

namespace {

struct PicerAec3 {
  int capture_sample_rate_hz;
  int render_sample_rate_hz;
  webrtc::scoped_refptr<webrtc::AudioProcessing> processing;
  std::vector<int16_t> render_output;
};

bool IsSupportedRate(int sample_rate_hz) {
  return sample_rate_hz == 8000 || sample_rate_hz == 16000 ||
         sample_rate_hz == 32000 || sample_rate_hz == 48000;
}

}  // namespace

void* picer_aec3_create(int capture_sample_rate_hz,
                        int render_sample_rate_hz) {
  if (!IsSupportedRate(capture_sample_rate_hz) ||
      !IsSupportedRate(render_sample_rate_hz)) {
    return nullptr;
  }

  webrtc::AudioProcessing::Config config;
  config.echo_canceller.enabled = true;
  config.high_pass_filter.enabled = true;
  config.noise_suppression.enabled = true;
  config.noise_suppression.level =
      webrtc::AudioProcessing::Config::NoiseSuppression::kHigh;

  auto processing = webrtc::BuiltinAudioProcessingBuilder(config).Build(
      webrtc::CreateEnvironment());
  if (!processing) {
    return nullptr;
  }

  return new PicerAec3{capture_sample_rate_hz, render_sample_rate_hz,
                       std::move(processing),
                       std::vector<int16_t>(render_sample_rate_hz / 100)};
}

void picer_aec3_destroy(void* handle) {
  delete static_cast<PicerAec3*>(handle);
}

int picer_aec3_process_render(void* handle,
                              const int16_t* samples,
                              int sample_count) {
  auto* state = static_cast<PicerAec3*>(handle);
  if (!state || !samples ||
      sample_count != state->render_sample_rate_hz / 100) {
    return webrtc::AudioProcessing::kBadParameterError;
  }
  webrtc::StreamConfig stream(state->render_sample_rate_hz, 1);
  return state->processing->ProcessReverseStream(
      samples, stream, stream, state->render_output.data());
}

int picer_aec3_process_capture(void* handle,
                               int16_t* samples,
                               int sample_count,
                               int stream_delay_ms) {
  auto* state = static_cast<PicerAec3*>(handle);
  if (!state || !samples ||
      sample_count != state->capture_sample_rate_hz / 100) {
    return webrtc::AudioProcessing::kBadParameterError;
  }
  state->processing->set_stream_delay_ms(std::clamp(stream_delay_ms, 0, 500));
  webrtc::StreamConfig stream(state->capture_sample_rate_hz, 1);
  return state->processing->ProcessStream(samples, stream, stream, samples);
}

const char* picer_aec3_version() {
  return "webrtc-aec3/picer-cabi-v1";
}
