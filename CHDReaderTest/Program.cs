using System;
using System.IO;

namespace CHDReaderTest;

internal class Program
{

    static void Main(string[] args)
    {
        // These files are hitting avHuff without flack
        // MAME - Rollback CHDs\MAME (v0.130) - cubeqst\cubeqst.chd
        // MAME - Rollback CHDs\MAME (v0.130) - firefox\firefox.chd
        // MAME - Rollback CHDs\MAME (v0.130) - mach3\mach3.chd
        // MAME - Rollback CHDs\MAME (v0.130) - usvsthem\usvsthem.chd
        //CHD.TestCHD(@"\MAME - Rollback CHDs\MAME (v0.130) - cubeqst\cubeqst.chd");

        // this CHD uses AVHUFF with FLAC
        //CHD.TestCHD(@"\\10.0.4.11\d$\RomVaultCHD\RomRoot\MAME - CHDs\MAME CHDs (merged)\cliffhgr\cliffhgr.chd");


        args = new string[] { @"\\10.0.4.11\d$\RomVaultCHD\RomRoot\testset\CHDSharp Test Suite" };


        if (args.Length == 0)
        {
            Console.WriteLine("Expecting a Directory to Scan");
            return;
        }

        foreach (string arg in args)
        {
            string sDir = arg.Replace("\"", "");

            DirectoryInfo di = new DirectoryInfo(sDir);
            checkdir(di, true);
        }
        Console.WriteLine("Done");
    }

    private static void checkdir(DirectoryInfo di, bool verify)
    {
        FileInfo[] fi = di.GetFiles("*.chd");
        foreach (FileInfo f in fi)
        {
            CHD.TestCHD(f.FullName);
        }

        DirectoryInfo[] arrdi = di.GetDirectories();
        foreach (DirectoryInfo d in arrdi)
        {
            checkdir(d, verify);
        }
    }
}
