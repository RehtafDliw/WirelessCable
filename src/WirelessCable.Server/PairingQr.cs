namespace WirelessCable.Server;

/// <summary>一次性的配对二维码。</summary>
/// <param name="Text">二维码承载的 JSON 文本（可用于复制/调试）。</param>
/// <param name="PngBytes">二维码 PNG 图像，供界面直接显示。</param>
/// <param name="ExpiresAtUnixMs">一次性随机数的过期时间。</param>
public sealed record PairingQr(string Text, byte[] PngBytes, long ExpiresAtUnixMs);
