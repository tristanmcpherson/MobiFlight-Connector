using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MobiFlight.Scripts
{
    internal class PythonEnvironmentValidator
    {
        private const string PYTHON_EXECUTABLE = "python";
        private const string POWERSHELL_EXECUTABLE = "powershell.exe";
        private const string PATH_ENV_VAR = "PATH";
        private const string PYTHON_KEYWORD = "python";
        private const string PYTHON_VERSION_ARG = "--version";
        private const string PIP_FREEZE_ARG = "-m pip freeze";
        private static readonly Version MINIMUM_PYTHON_VERSION = new Version(3, 11, 0);

        private readonly Dictionary<string, Version> RequiredPackages;

        public PythonEnvironmentValidator(Dictionary<string, Version> requiredPackages)
        {
            RequiredPackages = requiredPackages ?? throw new ArgumentNullException(nameof(requiredPackages));
        }

        public bool IsMinimumPythonVersion()
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = PYTHON_EXECUTABLE,
                Arguments = PYTHON_VERSION_ARG,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (Process process = Process.Start(start))
                {
                    using (StreamReader reader = process.StandardOutput)
                    {
                        //python --version returns "Python x.xx.x"
                        string output = reader.ReadToEnd();
                        var outputParts = output.Split(' ');
                        if (outputParts.Length > 1)
                        {
                            Log.Instance.log($"Python version: {outputParts[1]}.", LogSeverity.Info);
                            if (Version.TryParse(outputParts[1], out Version version))
                            {
                                if (version.CompareTo(MINIMUM_PYTHON_VERSION) >= 0)
                                {
                                    return true;
                                }
                                else
                                {
                                    Log.Instance.log($"Python version not supported: {outputParts[1]}.", LogSeverity.Warn);
                                    return false;
                                }
                            }
                        }
                        Log.Instance.log($"Failed to parse Python version: '{output}'.", LogSeverity.Warn);
                    }
                }
            }
            catch (Win32Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Python executable not found: {ex.Message}", LogSeverity.Error);
                return false;
            }
            catch (Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Error checking Python version: {ex.Message}", LogSeverity.Error);
                return false;
            }
            return false;
        }

        public bool IsPythonPathSet()
        {
            try
            {
                string pathVariable = Environment.GetEnvironmentVariable(PATH_ENV_VAR);
                if (string.IsNullOrEmpty(pathVariable))
                {
                    Log.Instance.log("PythonEnvironmentValidator - PATH environment variable is empty.", LogSeverity.Warn);
                    return false;
                }

                if (pathVariable.ToLower().Contains(PYTHON_KEYWORD))
                {
                    Log.Instance.log("PythonEnvironmentValidator - Python Path is set.", LogSeverity.Info);
                    return true;
                }
                else
                {
                    Log.Instance.log("PythonEnvironmentValidator - Python Path not set.", LogSeverity.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Error checking PATH variable: {ex.Message}", LogSeverity.Error);
                return false;
            }
        }

        public bool IsPythonMicrosoftStoreInstalled()
        {
            // *python.3* is used to match any Python 3 version installed from the Microsoft Store.
            // *python* is not used any more, since that gives a false positive on the PythonManager app.
            string powerShellCommand = "Get-AppxPackage -Name '*python.3*' | Select-Object Name";

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = POWERSHELL_EXECUTABLE,
                Arguments = $"-Command \"{powerShellCommand}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (Process process = Process.Start(start))
                {
                    using (StreamReader reader = process.StandardOutput)
                    {
                        string output = reader.ReadToEnd();

                        if (!string.IsNullOrEmpty(output) && output.Contains("Python"))
                        {
                            Log.Instance.log($"PythonEnvironmentValidator - Python Microsoft Store Version is installed: {output}", LogSeverity.Info);
                            return true;
                        }
                        else
                        {
                            Log.Instance.log("PythonEnvironmentValidator - Python Microsoft Store Version is not installed.", LogSeverity.Info);
                            return false;
                        }
                    }
                }
            }
            catch (Win32Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - PowerShell executable not found: {ex.Message}", LogSeverity.Error);
                return false;
            }
            catch (Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Error checking Microsoft Store Python: {ex.Message}", LogSeverity.Error);
                return false;
            }
        }

        public bool AreNecessaryPythonPackagesInstalled()
        {
            var installedPackages = new Dictionary<string, Version>();

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = PYTHON_EXECUTABLE,
                Arguments = PIP_FREEZE_ARG,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (Process process = Process.Start(start))
                {
                    using (StreamReader reader = process.StandardOutput)
                    {
                        string result = reader.ReadToEnd();
                        Log.Instance.log($"PythonEnvironmentValidator - Python installed packages: {Environment.NewLine}{result}", LogSeverity.Debug);
                        string[] lines = result.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                        {
                            var parts = line.Split(new string[] { "==" }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var v = parts[1].Split('.');
                                if (v.Length > 1)
                                {
                                    var majorSuccess = int.TryParse(v[0], out int major);
                                    var minorSuccess = int.TryParse(v[1], out int minor);
                                    if (majorSuccess && minorSuccess)
                                    {
                                        installedPackages.Add(parts[0], new Version(major, minor));
                                    }
                                    else
                                    {
                                        Log.Instance.log($"PythonEnvironmentValidator - Package version cannot be parsed: '{parts[1]}'", LogSeverity.Info);
                                    }
                                }
                                else
                                {
                                    Log.Instance.log($"PythonEnvironmentValidator - Package version has not two elements: '{parts[1]}'", LogSeverity.Error);
                                }
                            }
                            else
                            {
                                Log.Instance.log($"PythonEnvironmentValidator - Package info has not two elements: '{line}'", LogSeverity.Error);
                            }
                        }
                    }
                }
            }
            catch (Win32Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Python executable not found: {ex.Message}", LogSeverity.Error);
                return false;
            }
            catch (Exception ex)
            {
                Log.Instance.log($"PythonEnvironmentValidator - Error checking Python packages: {ex.Message}", LogSeverity.Error);
                return false;
            }

            return ValidatePackages(installedPackages);
        }

        public bool ValidatePackages(Dictionary<string, Version> installedPackages)
        {
            bool allPackagesAvailable = true;

            foreach (var package in RequiredPackages)
            {
                if (installedPackages.TryGetValue(package.Key, out var installedPackageVersion))
                {
                    if (installedPackageVersion < package.Value)
                    {
                        allPackagesAvailable = false;
                        Log.Instance.log($"PythonEnvironmentValidator - Python package version too low: '{package.Key}' (installed: {installedPackageVersion}, required: {package.Value})", LogSeverity.Error);
                    }
                }
                else
                {
                    allPackagesAvailable = false;
                    Log.Instance.log($"PythonEnvironmentValidator - Necessary Python package not installed: '{package.Key}' (required version: {package.Value})", LogSeverity.Error);
                }
            }

            return allPackagesAvailable;
        }

        public bool IsPythonEnvironmentReady()
        {
            if (!(IsPythonMicrosoftStoreInstalled() || IsPythonPathSet()))
            {
                return false;
            }

            if (!IsMinimumPythonVersion())
            {
                return false;
            }

            if (!AreNecessaryPythonPackagesInstalled())
            {
                return false;
            }

            return true;
        }
    }
}
