﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Management.Automation;

namespace Use_LinuxPipeline
{
    #region cmdlets
    [Cmdlet(VerbsLifecycle.Invoke, "NativeCommand")]
    [Alias("run")]
    [OutputType(typeof(int))]
    public class InvokeNativeCommand : PSCmdlet
    {
        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromRemainingArguments = true
        )]
        [ValidateNotNullOrEmpty]
        public string[] Argv { get; set; }

        [Parameter]
        public string WorkingDirectory { get; set; } = ".";

        [Parameter(ValueFromPipeline = true)]
        public int[] Pipes { get; set; } = null; // TODO: int[] -> int

        [Parameter]
        [ValidateNotNullOrEmpty]
        public string ErrorFile { get; set; }

        [Parameter]
        public SwitchParameter AppendError
        {
            get => appenderror;
            set => appenderror = value;
        }
        private bool appenderror;

        [Parameter]
        public SwitchParameter PipeError
        {
            get => pipeerror;
            set => pipeerror = value;
        }
        private bool pipeerror;

        protected override void ProcessRecord()
        {
            // 好像会自动恢复
            Directory.SetCurrentDirectory(WorkingDirectory);
            WriteObject(Core.Run(Pipes, Argv, pipeerror, ErrorFile, appenderror));
        }
    }

    [Cmdlet(VerbsCommunications.Receive, "RawPipeline")]
    [Alias("2ps")]
    public class ReceiveRawPipeline : PSCmdlet
    {
        [Parameter(Mandatory = true, ValueFromPipeline = true)]
        [ValidateNotNullOrEmpty]
        public int[] Pipes { get; set; }

        [Parameter(Mandatory = false, Position = 0)]
        public string Encoding { get; set; } = "UTF-8";

        [Parameter(Mandatory = false)]
        public SwitchParameter Raw
        {
            get => raw;
            set => raw = value;
        }
        private bool raw;

        protected override void ProcessRecord()
        {
            if (raw)
            {
                WriteObject(Core.GetOutputAsBytes(Pipes).ToArray());
            }
            else
            {
                Encoding encoding = System.Text.Encoding.GetEncoding(Encoding);
                foreach (string s in Core.GetOutputAsString(Pipes, encoding))
                {
                    WriteObject(s);
                }
            }
        }
    }

    [Cmdlet(VerbsCommon.Set, "RawPipelineToFile")]
    [Alias("out2")]
    public class SetRawPipelineToFile : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string Filename { get; set; }

        [Parameter(Mandatory = true, ValueFromPipeline = true)]
        [ValidateNotNullOrEmpty]
        public int[] Pipes { get; set; }

        protected override void ProcessRecord()
        {
            Stream fs = new FileStream(Filename, FileMode.Create, FileAccess.Write);
            Core.WriteToStream(Pipes, fs);
            fs.Close();
        }
    }

    [Cmdlet(VerbsCommon.Add, "RawPipelineToFile")]
    [Alias("add2")]
    public class AddRawPipelineToFile : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string Filename { get; set; }

        [Parameter(Mandatory = true, ValueFromPipeline = true)]
        [ValidateNotNullOrEmpty]
        public int[] Pipes { get; set; }

        protected override void ProcessRecord()
        {
            Stream fs = new FileStream(Filename, FileMode.Append);
            Core.WriteToStream(Pipes, fs);
            fs.Close();
        }
    }

    [Cmdlet(VerbsCommon.Get, "RawPipelineFromFile")]
    [Alias("stdin")]
    [OutputType(typeof(int))]
    public class GetRawPipelineFromFile : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string Filename { get; set; }

        protected override void ProcessRecord()
        {
            WriteObject(Core.ReadFile(Filename));
        }
    }
    #endregion cmdlets

    public static class Core
    {
        #region dllimport
        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dup(int oldfd);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dup2(int oldfd, int newfd);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pipe(int[] fd);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int open(string path, int oflag);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int close(int fd);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int execvp(string file, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern int fork();

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern void perror(string s);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern void exit(int status);

        [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
        private static extern void _exit(int status);

        #endregion dllimport

        public static int[] Run(int[] pipe_in, string[] cmd, bool pipe_error, string error_file, bool append_error)
        {
            int[] pipe_out = new int[2];
            if (pipe(pipe_out) == -1)
            {
                perror("bad pipe_out");
                exit(1);
            }

            int pid;
            if ((pid = fork()) == -1)
            {
                perror("bad fork");
                exit(1);
            }
            else if (pid == 0)
            {
                // set input
                if (pipe_in != null)
                {
                    dup2(pipe_in[0], 0);
                    close(pipe_in[0]);
                    close(pipe_in[1]);
                }
                // set output
                dup2(pipe_out[1], 1);
                close(pipe_out[1]);
                close(pipe_out[0]);

                int stderr_bak = dup(2);
                if (pipe_error)
                {
                    dup2(1, 2);
                }
                else if (error_file != null)
                {
                    int fd;
                    if (append_error)
                    {
                        if ((fd = open(error_file, 1 | 64 | 1024)) == -1) // O_WRONLY | O_CREAT | O_APPEND
                        {
                            perror("error with open(append mode)");
                            _exit(1);
                        }
                    }
                    else
                    {
                        if ((fd = open(error_file, 1 | 64)) == -1)
                        {
                            perror("error with open(overwrite mode)");
                            _exit(1);
                        }
                    }
                    dup2(fd, 2);
                    close(fd);
                }

                string[] argv = new string[cmd.Length + 1];
                cmd.CopyTo(argv, 0);
                argv[cmd.Length] = null;

                if (execvp(argv[0], argv) == -1)
                {
                    perror("there is something wrong with execlp");
                }

                if (append_error || error_file != null)
                {
                    dup2(stderr_bak, 2);
                }

                _exit(1);
            }

            if (pipe_in != null)
            {
                close(pipe_in[0]);
                close(pipe_in[1]);
            }

            return pipe_out;
        }

        public static int[] ReadFile(string filename)
        {
            int[] pipe_out = new int[2];
            if (pipe(pipe_out) == -1)
            {
                perror("bad pipe_out");
                exit(1);
            }

            int fd = open(filename, 0);
            dup2(fd, pipe_out[0]);
            close(fd);

            return pipe_out;
        }

        public static IEnumerable<byte> GetOutputAsBytes(int[] pipe_in)
        {
            int stdin_bak = dup(0);

            dup2(pipe_in[0], 0);
            close(pipe_in[0]);
            close(pipe_in[1]);

            Stream stdin = Console.OpenStandardInput();

            // 早点恢复, 避免 Ctrl + C 终止的时候造成 stdin 挂掉
            dup2(stdin_bak, 0);

            byte[] buf = new byte[512];
            int count;
            while ((count = stdin.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    yield return buf[i];
                }
            }

            stdin.Close();
        }

        public static IEnumerable<string> GetOutputAsString(int[] pipe_in, Encoding encoding)
        {
            int stdin_bak = dup(0);

            dup2(pipe_in[0], 0);
            close(pipe_in[0]);
            close(pipe_in[1]);

            Stream stdin = Console.OpenStandardInput();
            StreamReader reader = new StreamReader(stdin, encoding);

            // 早点恢复, 避免 Ctrl + C 终止的时候造成 stdin 挂掉
            dup2(stdin_bak, 0);

            string s;
            while ((s = reader.ReadLine()) != null)
            {
                yield return s;
            }

            reader.Close();
            stdin.Close();
        }

        public static void WriteToStream(int[] pipe_in, Stream stream)
        {
            int stdin_bak = dup(0);

            dup2(pipe_in[0], 0);
            close(pipe_in[0]);
            close(pipe_in[1]);

            Stream stdin = Console.OpenStandardInput();

            dup2(stdin_bak, 0);

            byte[] buffer = new byte[512];
            int count;
            while ((count = stdin.Read(buffer, 0, buffer.Length)) > 0)
            {
                stream.Write(buffer, 0, count);
            }

            stdin.Close();
        }
    }
}