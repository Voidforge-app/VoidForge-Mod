using UnityEngine;
using Object = UnityEngine.Object;

namespace BlueprintExtractor.Extraction;

/**
 * Extracts a Sprite to a PNG file by blitting it to a temporary RenderTexture and reading back pixels.
 * Handles atlas-packed sprites correctly by applying the sprite's UV offset and scale before readback.
 * This is synchronous and must be called from the main Unity thread.
 */
public static class TextureExtractor {
  /**
   * Saves a sprite as a PNG at the given output path.
   * If the sprite is atlas-packed, only its region is extracted.
   * Returns true on success, false if the sprite or its texture is null.
   */
  public static bool SaveSpriteToPng(Sprite sprite, string outputPath, int targetWidth = -1, int targetHeight = -1) {
    if (sprite == null || sprite.texture == null) return false;

    var spriteRect = sprite.textureRect;
    var width = targetWidth > 0 ? targetWidth : (int)spriteRect.width;
    var height = targetHeight > 0 ? targetHeight : (int)spriteRect.height;

    if (width <= 0 || height <= 0) return false;

    var uvScale = new Vector2(spriteRect.width / sprite.texture.width, spriteRect.height / sprite.texture.height);
    var uvOffset = new Vector2(spriteRect.x / sprite.texture.width, spriteRect.y / sprite.texture.height);

    var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
    Graphics.Blit(sprite.texture, renderTexture, uvScale, uvOffset);

    var previousActiveRenderTexture = RenderTexture.active;
    RenderTexture.active = renderTexture;

    try {
      var readableTexture = new Texture2D(width, height, TextureFormat.ARGB32, false);
      readableTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
      readableTexture.Apply();

      File.WriteAllBytes(outputPath, readableTexture.EncodeToPNG());
      Object.Destroy(readableTexture);

      return true;
    }
    finally {
      RenderTexture.active = previousActiveRenderTexture;
      RenderTexture.ReleaseTemporary(renderTexture);
    }
  }
}