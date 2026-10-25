using System.Text;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-73: what a file really is, from its first bytes ("magic bytes") - a document picked or downloaded as
    /// "photo.jpg" may be anything. Only the types an attachment may have are recognised.
    /// </summary>
    public static class FileSignatures
    {
        /// <summary>How many leading bytes <see cref="Detect"/> needs at most.</summary>
        public const int HeadLength = 1024;

        /// <summary>The cloud content type the bytes belong to ("image/jpeg", "application/pdf" …), or null.</summary>
        public static string? Detect(ReadOnlySpan<byte> head)
        {
            if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
                return "image/jpeg";
            if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return "image/png";
            if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
                return "image/webp";
            if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
            {
                var brand = Encoding.ASCII.GetString(head[8..12]);
                return brand switch
                {
                    "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" => "image/heic",
                    "mif1" or "msf1" => "image/heif",
                    _ => null,
                };
            }
            // PDF readers accept "%PDF-" anywhere in the first kilobyte (some files start with junk)
            if (head[..Math.Min(head.Length, HeadLength)].IndexOf("%PDF-"u8) >= 0)
                return "application/pdf";
            return null;
        }

        /// <summary>Reads up to <see cref="HeadLength"/> bytes from the start of a file (fewer when it's shorter).</summary>
        public static byte[] ReadHead(string path)
        {
            using var file = File.OpenRead(path);
            return ReadHead(file);
        }

        public static byte[] ReadHead(Stream stream)
        {
            var buffer = new byte[HeadLength];
            var filled = 0;
            int read;
            while (filled < buffer.Length && (read = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
                filled += read;
            return buffer[..filled];
        }
    }
}
