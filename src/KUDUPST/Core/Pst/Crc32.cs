namespace Kudupst.Core.Pst
{
    /// <summary>CRC-32 checksums (the polynomial used by zip files and by PST files).</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = MakeTable();

        private static uint[] MakeTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        /// <summary>The usual CRC-32 (as in zip files): starts from all ones and is inverted at the end.</summary>
        public static uint Standard(byte[] data) => ~Run(0xFFFFFFFF, data);

        /// <summary>The variant MS-PST uses for its own checksums: starts from zero, not inverted.</summary>
        public static uint Plain(byte[] data) => Run(0, data);

        private static uint Run(uint crc, byte[] data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
