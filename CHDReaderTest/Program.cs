using System;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace CHDReaderTest
{
    internal class Program
    {

        static unsafe void Main(string[] args)
        {
            //this fails because of missing AVHuff
            CHD.TestCHD(@"\\10.0.4.11\d$\RomVaultCHD\RomRoot\MAME - Rollback CHDs\MAME (v0.130) - cubeqst\cubeqst.chd");
            //CHD.TestCHD(@"\\10.0.4.11\d$\RomVaultCHD\RomRoot\MAME - CHDs\MAME CHDs (merged)\cliffhgr\cliffhgr.chd");


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
}
