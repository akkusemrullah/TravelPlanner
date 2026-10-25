using System.Text;

namespace TravelPlanner.Tests
{
    /// <summary>TP-73: attachment content that really is a PDF / photo (starts with the type's magic bytes).</summary>
    public static class TestFiles
    {
        public static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        public static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF, 0xE0 };
        public static readonly byte[] HeicMagic = { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' };

        public static byte[] PdfBytes(params byte[] body) => PdfMagic.Concat(body).ToArray();
        public static byte[] JpegBytes(params byte[] body) => JpegMagic.Concat(body).ToArray();

        public static MemoryStream Pdf(params byte[] body) => new(PdfBytes(body));
        public static MemoryStream Jpeg(params byte[] body) => new(JpegBytes(body));
        public static MemoryStream Heic(params byte[] body) => new(HeicMagic.Concat(body).ToArray());
    }
}
