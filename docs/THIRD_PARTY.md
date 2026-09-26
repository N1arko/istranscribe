# Third-party materials

The root [MIT license](../LICENSE) covers isTranscribe's own code and documentation. Dependencies and model weights keep their upstream licenses.

| Component | License material |
| --- | --- |
| whisper.cpp / ggml | [Pinned runtime manifest](../native/whisper/runtime-manifest.v1.json) and [upstream license](../native/whisper/licenses/whisper.cpp-LICENSE.txt) |
| OpenAI Whisper | [Upstream license](../native/whisper/licenses/openai-whisper-LICENSE.txt) |
| sherpa-onnx, ONNX Runtime, speaker models and linked dependencies | [Sources, build profile, and notices](../native/speaker/SOURCE-AND-NOTICES.md) |
| .NET / Avalonia / managed dependencies and fonts | [Windows notice policy and license materials](../packaging/windows/notices/) and [macOS notice policy](../packaging/macos/third-party-notice-policy.json) |

The release scripts assemble dependency inventories and notices from the pinned inputs. The speaker build collects the linked libraries' full license texts. Model weights are downloaded separately and have their own source, hash, and license metadata in the application.

The checked-in `piecewise.*` audio files are synthetic tone fixtures used by the audio decoder checks. User recordings and model weights are excluded from Git.
