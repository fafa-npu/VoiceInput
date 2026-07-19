# Third-party notices

- Microsoft Cognitive Services Speech SDK 1.50.0 is redistributed under the
  [Microsoft Speech SDK license](https://aka.ms/csspeech/license). Its bundled third-party notices
  are available at [Microsoft third-party notices](https://aka.ms/thirdpartynotices).
- FunASR llama.cpp runtime and the pinned GGUF/VAD artifacts retain the licenses linked from their
  upstream model cards. The catalog in `FunASR.swift` records each source and license URL.
- [transcribe.cpp 0.1.3](https://github.com/handy-computer/transcribe.cpp) and its bundled ggml,
  miniz, and backend components are redistributed under the license files shipped in the pinned
  `CTranscribe` XCFramework.
- [Qwen3-ASR 0.6B](https://huggingface.co/Qwen/Qwen3-ASR-0.6B) model weights are Apache-2.0.
  The app downloads the pinned Q8_0 GGUF from `handy-computer/Qwen3-ASR-0.6B-gguf` and verifies
  its SHA-256 digest before loading it.
