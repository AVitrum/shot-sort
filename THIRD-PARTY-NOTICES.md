# Third-party notices

ScreenSorter is licensed under the MIT License (see `LICENSE`). It uses the components below, which keep their own licenses.

## Included in the installer

| Component | License |
|---|---|
| .NET runtime and Windows Desktop runtime | MIT |
| Microsoft.Extensions.* | MIT |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw | Apache-2.0 |
| SQLite | Public domain |
| ONNX Runtime (Microsoft.ML.OnnxRuntime) | MIT |
| System.Numerics.Tensors | MIT |
| DirectML (Microsoft.AI.DirectML) | Microsoft Software License Terms: [package page](https://www.nuget.org/packages/Microsoft.AI.DirectML) |

The DirectML terms allow it to be distributed inside applications that run on Windows, and not as a stand-alone product.

## Downloaded or installed separately

ScreenSorter does not include these. Their own terms apply when you use them.

| Component | How it gets there | License |
|---|---|---|
| CLIP ViT-B/32 image model, ONNX conversion by [Xenova](https://huggingface.co/Xenova/clip-vit-base-patch32) | Downloaded by the app on first run | [OpenAI CLIP](https://github.com/openai/CLIP/blob/main/LICENSE): MIT |
| [LM Studio](https://lmstudio.ai/) and the vision model you choose | You install them | Their own terms |

## Used only to build and test

xUnit (Apache-2.0), coverlet (MIT), Microsoft.NET.Test.Sdk (MIT), [Inno Setup](https://jrsoftware.org/isinfo.php) (Inno Setup License).
