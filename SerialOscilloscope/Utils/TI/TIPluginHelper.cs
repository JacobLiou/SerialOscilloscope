using System.IO;

namespace SerialOscilloscope.Utils.TI
{
    public class TIPluginHelper
    {
        public static Task<bool> InvokeTIPluginAsync(string exePath, string exeArgs, string workDir, int timeoutSec)
        {
            var tcs = new TaskCompletionSource<bool>();

            new Thread(() =>
            {
                if (!File.Exists(exePath))
                {
                    tcs.SetException(new Exception($"Executable Not Found：\"{exePath}\""));
                    return;
                }

                using var tiProcess = new System.Diagnostics.Process();

                tiProcess.StartInfo.FileName = exePath;
                tiProcess.StartInfo.Arguments = exeArgs;
                tiProcess.StartInfo.WorkingDirectory = workDir;
                //tiProcess.StartInfo.CreateNoWindow = false;
                //tiProcess.StartInfo.UseShellExecute = true;
                tiProcess.StartInfo.CreateNoWindow = true;
                tiProcess.StartInfo.UseShellExecute = false;
                tiProcess.StartInfo.RedirectStandardError = true;
                tiProcess.StartInfo.RedirectStandardOutput = true;

                var logFile = File.CreateText(Path.Combine(workDir, Path.GetFileNameWithoutExtension(exePath) + ".log"));
                tiProcess.Start();

                logFile.WriteLine(tiProcess.StandardOutput.ReadToEnd());

                tiProcess.WaitForExit(timeoutSec * 1000);

                if (tiProcess.HasExited)
                {
                    if (tiProcess.ExitCode != 0)
                    {
                        var errMsg = tiProcess.StandardError.ReadToEnd();
                        tcs.SetException(new Exception(
                            $"The \"{Path.GetFileName(exePath)}\" process exited incorrectly with followed error message:  " + errMsg));
                    }
                    else
                    {
                        tcs.SetResult(true);
                    }
                }
                else
                {
                    tiProcess.Kill();
                    tcs.SetException(new Exception($"The \"{Path.GetFileName(exePath)}\" process was killed due to timeout."));
                }

                logFile.Close();


            }).Start();

            return tcs.Task;
        }

        public static Task<bool> ConvertCoffToXmlAsync(string coffInput, string dwarfXmlOutput, string workDir, int timeoutSec = 25)
        {
            return InvokeTIPluginAsync(
                exePath: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resource\TI", "ofd2000.exe"),
                exeArgs: $"-gx -o {Path.GetFileName(dwarfXmlOutput)} {Path.GetFileName(coffInput)}",
                workDir: workDir,
                timeoutSec: timeoutSec
            );

        }

        public static Task<bool> ConvertCoffToHexAsync(string coffInputPath, string hexOutputPath, string workDir, int timeoutSec = 25)
        {
            return InvokeTIPluginAsync(
                exePath: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resource\TI", "hex2000.exe"),
                exeArgs: $"-romwidth 16 -memwidth 16 -i -o {Path.GetFileName(hexOutputPath)} {Path.GetFileName(coffInputPath)}",
                workDir: workDir,
                timeoutSec: timeoutSec
            );

        }

        public static Task<bool> ConvertHexToBinAsync(string hexInputPath, string binOutputPath, string workDir,
                                                        uint flashStartAddr = 0x86000, uint flashEndAddr = 0xBFFFD, int timeoutSec = 25)
        {
            return InvokeTIPluginAsync(
                exePath: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resource\TI", "c2000_hex2bin.exe"),
                exeArgs: $"{Path.GetFileName(hexInputPath)} {Path.GetFileName(binOutputPath)} 0x{flashStartAddr:X8} 0x{flashEndAddr:X8} ",
                workDir: workDir,
                timeoutSec: timeoutSec
            );
        }


    }
}