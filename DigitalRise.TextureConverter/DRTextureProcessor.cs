// DigitalRune Engine - Copyright (C) DigitalRune GmbH
// This file is subject to the terms and conditions defined in
// file 'LICENSE.TXT', which is part of this source code package.

using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Graphics;


namespace DigitalRise.TextureConverter
{
	/// <summary>
	/// Processes a model texture.
	/// </summary>
	internal class DRTextureProcessor
	{
		// TODO: Add supported for BC2 (DXT3)?
		// Currently either BC1 (DXT1, opaque or binary alpha) or BC3 (DXT5, 8-bit alpha) are used.
		// BC3 alpha values are interpolated. BC2 offers 4 bit non-interpolated alpha, which is better
		// for sharp alpha masks. (Though some consider BC2 a relic of the past.)


		//--------------------------------------------------------------
		#region Properties & Events
		//--------------------------------------------------------------

		/// <summary>
		/// Gets or sets the color used when color keying for a texture is enabled. When color keying, 
		/// all pixels of a specified color are replaced with transparent black.
		/// </summary>
		/// <value>Color value of the material to replace with transparent black.</value>
		[DefaultValue(typeof(Color), "255, 0, 255, 255")]
		[DisplayName("Color Key Color")]
		[Description("If the texture is color keyed, pixels of this color are replaced with transparent black.")]
		public virtual Color ColorKeyColor
		{
			get { return _colorKeyColor; }
			set { _colorKeyColor = value; }
		}
		private Color _colorKeyColor = Color.Magenta;


		/// <summary>
		/// Gets or sets a value indicating whether color keying of a texture is enabled.
		/// </summary>
		/// <value>
		/// <see langword="true"/> if color keying is enabled; <see langword="false"/> otherwise.
		/// </value>
		[DefaultValue(false)]
		[DisplayName("Color Key Enabled")]
		[Description("If enabled, the texture is color keyed. Pixels matching the value of \"Color Key Color\" are replaced with transparent black.")]
		public virtual bool ColorKeyEnabled
		{
			get { return _colorKeyEnabled; }
			set { _colorKeyEnabled = value; }
		}
		private bool _colorKeyEnabled;

		#endregion

		//--------------------------------------------------------------
		#region Methods
		//--------------------------------------------------------------

		/// <summary>
		/// Processes a texture.
		/// </summary>
		/// <param name="input">The texture content to process.</param>
		/// <param name="profile">Graphics profile.</param>
		/// <returns>The converted texture content.</returns>
		/// <exception cref="ArgumentNullException">
		/// <paramref name="input"/> or <paramref name="profile"/> is <see langword="null"/>.
		/// </exception>
		public Texture Process(TextureContent input, Options options)
		{
			if (input == null)
				throw new ArgumentNullException("input");

			// Linear vs. sRGB:
			// XNA does not support _SRGB texture formats. Texture processing is designed
			// for non-sRGB formats. (sRGB formats require a different order of operations!
			// See section "Alpha Blending" in DigitalRune KB.)

			try
			{
				var mipmapChain = input.Faces[0]; // Mipmap chain.
				var level0 = mipmapChain[0];      // Most detailed mipmap level.
				int width = level0.Width;
				int height = level0.Height;

				var texture = TextureHelper.ToTexture(input);

				var format = options.Format;
				// Early out?
				if (!ColorKeyEnabled
					&& (!options.GenerateMipmaps || (width == 1 && height == 1) // Does not need mipmaps
						|| mipmapChain.Count > 1)                       // or already has mipmaps.
					&& !options.PremultiplyAlpha
					&& (!options.ResizeToPowerOfTwo || (MathUtility.IsPowerOf2(width) && MathUtility.IsPowerOf2(height)))
					&& !options.ScaleAlphaToCoverage)
				{
					if (format == DRTextureFormat.NoChange)
					{
						// No processing required.
						return texture;
					}

					SurfaceFormat surfaceFormat;
					if (!level0.TryGetFormat(out surfaceFormat))
						throw new InvalidContentException("Surface format is not supported.", input.Identity);

					if ((format == DRTextureFormat.Color && surfaceFormat == SurfaceFormat.Color)
						|| (format == DRTextureFormat.Dxt && IsDxt(surfaceFormat)))
					{
						// No processing required.
						return texture;
					}
				}

				var sourceFormat = texture.Description.Format;

				// Apply color keying.
				if (ColorKeyEnabled)
				{
					// Apply color keying in RGBA 8:8:8:8.
					texture = texture.ConvertTo(DataFormat.R8G8B8A8_UNORM);
					TextureHelper.ApplyColorKey(texture, ColorKeyColor.R, ColorKeyColor.G, ColorKeyColor.B, ColorKeyColor.A);
				}

				// Normal maps require special treatment (no sRGB, etc.).
				bool isNormalMap = (format == DRTextureFormat.Normal || format == DRTextureFormat.NormalInvertY);
				if (isNormalMap)
				{
					options.InputGamma = 1.0f;
					options.OutputGamma = 1.0f;
					options.PremultiplyAlpha = false;
				}

				// Check whether alpha channel is used.
				bool hasAlpha = false;            // true if alpha channel != 1.
				bool hasFractionalAlpha = false;  // true if alpha channel has 0 < alpha < 1.
				if (!isNormalMap)
				{
					if (options.GenerateMipmaps || options.ResizeToPowerOfTwo || options.PremultiplyAlpha || format == DRTextureFormat.Dxt)
					{
						try
						{
							TextureHelper.HasAlpha(texture, out hasAlpha, out hasFractionalAlpha);
						}
						catch (NotSupportedException)
						{
							// HasAlpha() does not support the current format. Convert and try again.
							texture = texture.ConvertTo(DataFormat.R32G32B32A32_FLOAT);
							TextureHelper.HasAlpha(texture, out hasAlpha, out hasFractionalAlpha);
						}
					}
				}

				// Convert to high-precision, floating-point format for processing.
				texture = texture.ConvertTo(DataFormat.R32G32B32A32_FLOAT);

				if (!isNormalMap)
				{
					// Convert texture from gamma space to linear space.
					TextureHelper.GammaToLinear(texture, options.InputGamma);
				}
				else
				{
					// Convert normal map from [0, 1] to [-1, 1].
					TextureHelper.UnpackNormals(texture);
				}

				// The resize filter needs to consider alpha if the image is not already
				// premultiplied. options.PremultiplyAlpha indicates that the source image has
				// alpha, but is not yet premultiplied. (Premultiplication happen at the
				// end.)
				bool alphaTransparency = hasAlpha && options.PremultiplyAlpha;

				if (options.ResizeToPowerOfTwo)
				{
					// Resize to power-of-two.
					int expectedWidth = RoundUpToPowerOfTwo(texture.Description.Width);
					int expectedHeight = RoundUpToPowerOfTwo(texture.Description.Height);
					if (expectedWidth != texture.Description.Width || expectedHeight != texture.Description.Height)
						texture = texture.Resize(expectedWidth, expectedHeight, texture.Description.Depth, ResizeFilter.Kaiser, alphaTransparency, TextureAddressMode.Clamp);
				}

				if (format == DRTextureFormat.Dxt || format == DRTextureFormat.Normal || format == DRTextureFormat.NormalInvertY)
				{
					// Resize to multiple of four.
					int expectedWidth = RoundToMultipleOfFour(texture.Description.Width);
					int expectedHeight = RoundToMultipleOfFour(texture.Description.Height);
					if (expectedWidth != texture.Description.Width || expectedHeight != texture.Description.Height)
						texture = texture.Resize(expectedWidth, expectedHeight, texture.Description.Depth, ResizeFilter.Kaiser, alphaTransparency, TextureAddressMode.Clamp);
				}

				if (options.GenerateMipmaps && texture.Description.MipLevels <= 1)
				{
					// Generate mipmaps.
					texture.GenerateMipmaps(ResizeFilter.Box, alphaTransparency, TextureAddressMode.Repeat);
				}

				// For debugging:
				// ColorizeMipmaps(texture);

				if (!isNormalMap)
				{
					if (options.ScaleAlphaToCoverage)
						TextureHelper.ScaleAlphaToCoverage(texture, options.ReferenceAlpha, /* data not yet premultiplied */ false);

					// Convert texture from linear space to gamma space.
					TextureHelper.LinearToGamma(texture, options.OutputGamma);

					// Premultiply alpha.
					if (hasAlpha && options.PremultiplyAlpha)
						TextureHelper.PremultiplyAlpha(texture);
				}
				else
				{
					// Renormalize normal map and convert to DXT5nm.
					TextureHelper.ProcessNormals(texture, format == DRTextureFormat.NormalInvertY);
				}

				// Convert to from floating-point format to requested output format.
				switch (format)
				{
					case DRTextureFormat.NoChange:
						texture = texture.ConvertTo(sourceFormat);
						break;

					case DRTextureFormat.Color:
						texture = texture.ConvertTo(DataFormat.R8G8B8A8_UNORM);
						break;

					case DRTextureFormat.Dxt:
						if (texture.Description.Dimension == TextureDimension.Texture3D)
						{
							texture = texture.ConvertTo(DataFormat.R8G8B8A8_UNORM);
						}
						else
						{
#if MONOGAME
							input = Compress(context, texture, hasAlpha, hasFractionalAlpha, options.PremultiplyAlpha, input.Identity);
#else
							if (hasFractionalAlpha)
								texture = texture.ConvertTo(DataFormat.BC3_UNORM);
							else
								texture = texture.ConvertTo(DataFormat.BC1_UNORM);

#endif
						}
						break;

					case DRTextureFormat.Normal:
					case DRTextureFormat.NormalInvertY:
#if MONOGAME
						input = Compress(context, texture, true, true, false, input.Identity);
#else
						texture = texture.ConvertTo(DataFormat.BC3_UNORM);
#endif
						break;
					default:
						throw new NotSupportedException("The specified output format is not supported.");
				}

				return texture;
			}
			catch (Exception ex)
			{
				throw new InvalidContentException(ex.Message, input.Identity);
			}
		}


		private static bool IsDxt(SurfaceFormat format)
		{
			switch (format)
			{
				case SurfaceFormat.Dxt1:
				case SurfaceFormat.Dxt3:
				case SurfaceFormat.Dxt5:
					return true;
				default:
					return false;
			}
		}

		// To debug mipmap problems, fill each level with a constant color.
		private static void ColorizeMipmaps(TextureContent texture)
		{
			foreach (MipmapChain mipmaps in texture.Faces)
			{
				for (int i = 1; i < mipmaps.Count; i++)
				{
					Vector4 color;
					switch (i)
					{
						case 1: color = Color.White.ToVector4(); break;
						case 2: color = Color.Green.ToVector4(); break;
						case 3: color = Color.Pink.ToVector4(); break;
						case 4: color = Color.Cyan.ToVector4(); break;
						case 5: color = Color.Magenta.ToVector4(); break;
						case 6: color = Color.Yellow.ToVector4(); break;
						case 7: color = Color.Red.ToVector4(); break;
						case 8: color = Color.Blue.ToVector4(); break;
						case 9: color = Color.Gray.ToVector4(); break;
						case 10: color = Color.DarkRed.ToVector4(); break;
						case 11: color = Color.Turquoise.ToVector4(); break;
						default: color = Color.Orange.ToVector4(); break;
					}

					var bitmap = (PixelBitmapContent<Vector4>)mipmaps[i];
					for (int y = 0; y < bitmap.Height; y++)
					{
						Vector4[] row = bitmap.GetRow(y);
						for (int x = 0; x < row.Length; x++)
							row[x] = color;
					}
				}
			}
		}


		private static int RoundUpToPowerOfTwo(int value)
		{
			return (int)MathUtility.NextPowerOf2((uint)value - 1);
		}


		private static int RoundToMultipleOfFour(int value)
		{
			// http://stackoverflow.com/questions/2022179/c-quick-calculation-of-next-multiple-of-4
			value = (value + 3) & ~0x3;
			Debug.Assert((value % 4) == 0);
			return value;
		}


		//private static void Save(Texture texture, string fileName)
		//{
		//  texture = texture.ConvertTo(DataFormat.R8G8B8A8_UNORM);
		//  using (var stream = System.IO.File.OpenWrite(fileName))
		//  {
		//    DdsHelper.Save(texture, stream, DdsFlags.None);
		//  }
		//}


#if MONOGAME
		private static TextureContent Compress(ContentProcessorContext context, Texture texture, bool hasAlpha, bool hasFractionalAlpha, bool premultipliedAlpha, ContentIdentity identity)
		{
			TextureContent textureContent;
			switch (context.TargetPlatform)
			{
				case TargetPlatform.Windows:
				case TargetPlatform.DesktopGL:
				case TargetPlatform.MacOSX:
				case TargetPlatform.NativeClient:
				case TargetPlatform.Xbox360:
					context.Logger.LogMessage("Using DXT Compression.");
					if (hasAlpha && hasFractionalAlpha)
					{
						texture = texture.ConvertTo(DataFormat.BC3_UNORM);
						textureContent = TextureHelper.ToContent(texture, identity);
					}
					else
					{
						texture = texture.ConvertTo(DataFormat.BC1_UNORM);
						textureContent = TextureHelper.ToContent(texture, identity);
					}
					break;

				case TargetPlatform.iOS:
					try
					{
						context.Logger.LogMessage("Using PVRTC Compression.");

						// Limitations set by Apple.
						var description = texture.Description;
						if (!MathUtility.IsPowerOf2(description.Width) || !MathUtility.IsPowerOf2(description.Height))
							throw new ArgumentException("PVRTC texture compression failed. Texture width and height need to be a power of two.");
						if (description.Width != description.Height)
							throw new ArgumentException("PVRTC texture compression failed. Texture must be square.");
						if (description.Width < 8)
							throw new ArgumentException("PVRTC texture compression failed. Texture width and height must be at least 8.");

						// TODO: PvrtcRgb*BitmapContent assumes premultiplied alpha. But the PVRTexLib can compress straight and premultiplied alpha.

						if (hasAlpha)
							textureContent = Compress(typeof(PvrtcRgba4BitmapContent), texture, identity);
						else
							textureContent = Compress(typeof(PvrtcRgb4BitmapContent), texture, identity);
					}
					catch (ArgumentException exception)
					{
						// Apple sets very strict limitations. Fall back to Color if format is not supported.
						context.Logger.LogWarning(null, identity, "{0} Using Color format instead.", exception.Message);
						texture = texture.ConvertTo(DataFormat.R8G8B8A8_UNORM);
						textureContent = TextureHelper.ToContent(texture, identity);
					}
					break;

				case TargetPlatform.Android:
					// The most widely available format on Android is ETC1.
					// Other formats (ATC, PVRTC, S3TC/DXT) is not available on all devices.
					// Reference: http://developer.android.com/guide/topics/graphics/opengl.html
					if (hasAlpha)
					{
						// ETC1 does not support alpha. What's is the best format for encoding alpha?
						// MonoGame uses B4G4R4A4_UNorm.
						// TODO: This path is also used for normal maps on Android. (Normal maps not yet supported.)
						context.Logger.LogMessage("Using Bgra4444 format.");
						texture = texture.ConvertTo(DataFormat.B4G4R4A4_UNORM);
						textureContent = TextureHelper.ToContent(texture, identity);
					}
					else
					{
						context.Logger.LogMessage("Using ETC1 Compression.");
						textureContent = Compress(typeof(Etc1BitmapContent), texture, identity);
					}
					break;

				default:
					throw new NotImplementedException(string.Format("Texture compression it not implemented for {0}", context.TargetPlatform));
			}

			return textureContent;
		}


		private static TextureContent Compress(Type bitmapContentType, Texture texture, ContentIdentity identity)
		{
			// Let MonoGame's BitmapContent handle the compression.
			var description = texture.Description;
			switch (description.Dimension)
			{
				case TextureDimension.Texture1D:
				case TextureDimension.Texture2D:
					{
						var textureContent = new Texture2DContent { Identity = identity };
						for (int mipIndex = 0; mipIndex < description.MipLevels; mipIndex++)
						{
							var image = texture.Images[texture.GetImageIndex(mipIndex, 0, 0)];
							var sourceBitmap = TextureHelper.ToContent(image);
							var targetBitmap = (BitmapContent)Activator.CreateInstance(bitmapContentType, image.Width, image.Height);
							BitmapContent.Copy(sourceBitmap, targetBitmap);
							textureContent.Mipmaps.Add(targetBitmap);
						}

						return textureContent;
					}
				case TextureDimension.TextureCube:
					{
						var textureContent = new TextureCubeContent { Identity = identity };
						for (int faceIndex = 0; faceIndex < 6; faceIndex++)
						{
							for (int mipIndex = 0; mipIndex < description.MipLevels; mipIndex++)
							{
								var image = texture.Images[texture.GetImageIndex(mipIndex, faceIndex, 0)];
								var sourceBitmap = TextureHelper.ToContent(image);
								var targetBitmap = (BitmapContent)Activator.CreateInstance(bitmapContentType, image.Width, image.Height);
								BitmapContent.Copy(sourceBitmap, targetBitmap);
								textureContent.Faces[faceIndex].Add(targetBitmap);
							}
						}

						return textureContent;
					}
				case TextureDimension.Texture3D:
					{
						var textureContent = new Texture3DContent { Identity = identity };
						for (int zIndex = 0; zIndex < description.Depth; zIndex++)
						{
							textureContent.Faces.Add(new MipmapChain());
							for (int mipIndex = 0; mipIndex < description.MipLevels; mipIndex++)
							{
								var image = texture.Images[texture.GetImageIndex(mipIndex, 0, zIndex)];
								var sourceBitmap = TextureHelper.ToContent(image);
								var targetBitmap = (BitmapContent)Activator.CreateInstance(bitmapContentType, image.Width, image.Height);
								BitmapContent.Copy(sourceBitmap, targetBitmap);
								textureContent.Faces[zIndex].Add(targetBitmap);
							}
						}

						return textureContent;
					}
			}

			throw new InvalidOperationException("Invalid texture dimension.");
		}
#endif
		#endregion
	}
}
