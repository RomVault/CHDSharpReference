using System;
using System.IO;

namespace CHDReaderTest
{
    public static class CHD
    {
        public static void TestCHD(string filename)
        {
            Console.WriteLine($"Testing :{filename}");
            using (Stream s = File.Open(filename, FileMode.Open, FileAccess.Read))
            {
                if (!CHDVersion.CheckHeader(s, out uint length, out uint version))
                    return;

                Console.WriteLine($@"CHD Version {version}");

                bool valid = false;
                switch (version)
                {
                    case 1:
                        valid = CHDV1.go(s);
                        break;
                    case 2:
                        valid = CHDV2.go(s);
                        break;
                    case 3:
                        valid = CHDV3.go(s);
                        break;
                    case 4:
                        valid = CHDV4.go(s);
                        break;
                    default:
                        Console.WriteLine($"Unknown version {version}");
                        return;
                }
                if (!valid)
                    Console.WriteLine($@"Valid = {valid}");
            }
        }
    }

    [Flags]
    public enum mapFlags
    {
        MAP_ENTRY_FLAG_TYPE_MASK = 0x000f,      /* what type of hunk */
        MAP_ENTRY_FLAG_NO_CRC = 0x0010,         /* no CRC is present */

        MAP_ENTRY_TYPE_INVALID = 0x0000,        /* invalid type */
        MAP_ENTRY_TYPE_COMPRESSED = 0x0001,     /* standard compression */
        MAP_ENTRY_TYPE_UNCOMPRESSED = 0x0002,   /* uncompressed data */
        MAP_ENTRY_TYPE_MINI = 0x0003,           /* mini: use offset as raw data */
        MAP_ENTRY_TYPE_SELF_HUNK = 0x0004,      /* same as another hunk in this file */
        MAP_ENTRY_TYPE_PARENT_HUNK = 0x0005     /* same as a hunk in the parent file */
    }

    public enum hdErr
    {
        HDERR_NONE,
        HDERR_READ_ERROR,
        HDERR_DECOMPRESSION_ERROR,
        HDERR_UNSUPPORTED
    };

}
