# MockProof

The image proof of the interface. The mock images are the specification: `refine-light.png`, `refine-dark.png` and the other views.

- `Run-MockProof.ps1` builds this tool, runs it on the lab client under the capture lock and copies the images back. The tool starts the
  real application with an empty data folder and fills it with the rows and texts of the mock (`MockData.cs`).
- `Compare-Mock.ps1` writes for each screen a side by side image, an overlay that marks each differing pixel, and a half-and-half blend.
- Each screen is a method with the `[Screen("name")]` attribute in a `Scene.<group>.cs` file.

A screen passes when a reviewer who gets only the two images finds no difference. A passed function test is not a match with the mock.
