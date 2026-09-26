# Speaker processing runtime

The isolated worker uses the C# binding from `org.k2fsa.sherpa.onnx/1.12.14`.
Its native code is built from sherpa-onnx commit
`26aa2fa93210376a89de3a65a1a4dd320c37f5e9` with TTS, PortAudio, WebSocket,
Python and GPU disabled. `EIGEN_MPL2_ONLY` is defined. The all-features native
binaries supplied by the NuGet package are excluded from the product.

Source archive:
https://codeload.github.com/k2-fsa/sherpa-onnx/tar.gz/26aa2fa93210376a89de3a65a1a4dd320c37f5e9

Archive SHA-256: `7c2daea812195ebef5f0799e68a907319e732b152176b0efa4e1dd7660df6572`.
Dependency source URLs and exact content hashes are in that archive's `cmake/`
directory. The included Eigen source is unmodified; its exact source is selected
by `cmake/eigen.cmake` and distributed under MPL-2.0. Build configuration changes
are recorded above. Full license materials for linked dependencies accompany
this document; the generated package inventory records their SHA-256 hashes.

The speech-segmentation and voice-embedding models are separate on-demand assets.
They are excluded from the installer. Their pinned URLs, sizes, hashes and license
identifiers are recorded in the app-owned `DiarizationAssets.Manifest`.

- sherpa-onnx and C# binding: Apache-2.0, k2-fsa contributors.
- ONNX Runtime: MIT, Microsoft; bundled ThirdPartyNotices are included.
- kaldi-native-fbank, kaldi-decoder, kaldifst, OpenFst, simple-sentencepiece:
  Apache-2.0; complete upstream notices accompany this file.
- KISS FFT: BSD-3-Clause, Mark Borgerding.
- hclust-cpp: BSD-2-Clause, Daniel Müllner and Christoph Dalitz.
- Eigen: MPL-2.0-only build profile.
- cppjieba and limonp: MIT; complete upstream notices accompany this file.

Model sources and notices:

- Pyannote segmentation ONNX, MIT:
  https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/tree/9403a6902bb58e3d5ae8c7e77c3422de279db2e0
- WeSpeaker ResNet34 voice embeddings, Apache-2.0:
  https://github.com/wenet-e2e/wespeaker

No eSpeak NG or Piper code is linked into this native build.

## Pyannote segmentation model license

MIT License

Copyright (c) 2022 CNRS

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

WeSpeaker model: WeSpeaker contributors, Apache-2.0. The complete Apache-2.0
license accompanies this notice in `speaker_upstream-src-LICENSE.txt`.
