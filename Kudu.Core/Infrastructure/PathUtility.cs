﻿using System;
using System.IO;
using SystemEnvironment = System.Environment;

namespace Kudu.Core.Infrastructure
{
    internal static class PathUtility
    {
        internal static string ResolveGitPath()
        {
            string programFiles = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.ProgramFilesX86);
            string path = Path.Combine(programFiles, "Git", "bin", "git.exe");
            // throw new InvalidOperationException(Resources.Error_FailedToLocateGit);

            if (!File.Exists(path))
            {
                throw new InvalidOperationException(Resources.Error_FailedToLocateGit);
            }

            return path;
        }

        internal static string ResolveSSHPath()
        {
            string programFiles = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.ProgramFilesX86);
            string path = Path.Combine(programFiles, "Git", "bin", "ssh.exe");

            if (!File.Exists(path))
            {
                throw new InvalidOperationException(Resources.Error_FailedToLocateSsh);
            }

            return path;
        }
        
        internal static string ResolveNpmPath()
        {
            string programFiles = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.ProgramFilesX86);
            return Path.Combine(programFiles, "nodejs", "npm.cmd");
        }

        internal static string ResolveMSBuildPath()
        {
            string windir = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.Windows);
            return Path.Combine(windir, @"Microsoft.NET", "Framework", "v4.0.30319", "MSBuild.exe");
        }

        internal static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        }
    }
}
