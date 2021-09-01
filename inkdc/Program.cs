using System;
using System.IO;
using Ink.Runtime;

namespace inkdc
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: inkdc <JSON file>");
                return;
            }

            if (Directory.Exists(args[0]))
            {
                VerifyTestcases(args[0]);

            }
            else
            {
                var text = File.ReadAllText(args[0]);
                Console.WriteLine(DecompileStory(text));
            }
        }

        private static string DecompileStory(string json)
        {
            var story = new Ink.Runtime.Story(json);
            var decompiler = new StoryDecompiler(story);
            return decompiler.DecompileRoot();
        }

        static void VerifyTestcases(string path)
        {
            foreach (string file in Directory.EnumerateFiles(path))
            {
                if (file.EndsWith(".ink"))
                {
                    var ink = File.ReadAllText(file);
                    var json = File.ReadAllText(file + ".json");
                    try
                    {
                        var decompiledInk = DecompileStory(json);
                        if (ink != decompiledInk)
                        {
                            Console.WriteLine("Decompilation result differs from source for " + file + ":");
                            Console.WriteLine(decompiledInk);
                        }
                        else
                        {
                            Console.WriteLine("OK " + file);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERR " + file + ":\n" + ex.StackTrace);
                    }
                }
            }
                
        }
    }
}


