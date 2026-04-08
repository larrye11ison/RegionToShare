using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var sourceName = args[0];
var targetSize = int.Parse(args[1]);
var targetName = args[2];

using var sourceImage = Image.Load<Rgba32>(sourceName);
using var targetImage = new Image<Rgba32>(targetSize, targetSize);

var xOffset = (targetImage.Width - sourceImage.Width) / 2;
var yOffset = (targetImage.Height - sourceImage.Height) / 2;

// Use ImageSharp's drawing API to composite the source image onto the centered target image.
targetImage.Mutate(ctx => ctx.DrawImage(sourceImage, new Point(xOffset, yOffset), 1f));

targetImage.SaveAsPng(targetName);
