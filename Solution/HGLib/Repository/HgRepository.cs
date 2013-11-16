﻿using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Timers;
using System.Windows.Forms;

namespace HgLib
{
    public class HgRepository
    {
        public event EventHandler StatusChanged = (s, e) => { };
        
        private const int UpdateInterval = 2000;

        private bool _updating;
        private HgCommandQueue _commands;
        private DirectoryWatcherMap _directoryWatchers;
        private HgFileInfoDictionary _cache;
        private Dictionary<string, string> _roots;

        private System.Timers.Timer _timer;

        private volatile bool _cacheUpdateRequired;


        public bool IsEmpty
        {
            get { return _directoryWatchers.Count == 0; }
        }
        
        public bool CacheUpdateRequired
        {
            set { _cacheUpdateRequired = value; }
        }

        public bool FileSystemWatch
        {
            set { _directoryWatchers.FileSystemWatch = value; }
        }


        public HgRepository()
        {
            Initialize();

            _timer.Start();
        }

        private void Initialize()
        {
            _cache = new HgFileInfoDictionary();
            _directoryWatchers = new DirectoryWatcherMap();
            _commands = new HgCommandQueue();
            _roots = new Dictionary<string, string>();
            
            _timer = new System.Timers.Timer
            { 
                AutoReset = false,
                Interval = 100,
            };

            _timer.Elapsed += OnTimerElapsed;
        }


        public void Enqueue(HgCommand command)
        {
            lock (_commands)
            {
                _commands.Enqueue(command);
            }
        }


        public void AddFiles(string[] fileNames)
        {
            fileNames = FilterIgnored(fileNames);

            if (fileNames.Length == 0)
            {
                return;
            }

            try
            {
                BeginUpdate();
                Cache(Hg.AddFiles(fileNames));
            }
            finally
            {
                EndUpdate();
            }
        }
        
        public void AddRootDirectory(string directory)
        {
            if (String.IsNullOrEmpty(directory))
            {
                return;
            }

            var root = HgProvider.FindRepositoryRoot(directory);

            if (String.IsNullOrEmpty(root) || _roots.ContainsKey(root))
            {
                return;
            }

            _roots[root] = Hg.GetCurrentBranchName(root);

            if (!_directoryWatchers.ContainsDirectory(root))
            {
                _directoryWatchers.WatchDirectory(root);
            }

            Cache(Hg.GetRootStatus(root));
        }

        public void RemoveFiles(string[] fileNames)
        {
            var moved = new List<string>();
            var removed = new List<string>();
            var renamed = new List<string>();

            lock (_cache)
            {
                foreach (var fileName in fileNames)
                {
                    _cache.Remove(fileName);

                    string newName;

                    if (_cache.FileMoved(fileName, out newName))
                    {
                        moved.Add(fileName);
                        renamed.Add(newName);
                    }
                    else
                    {
                        removed.Add(fileName);
                    }
                }
            }

            if (moved.Count > 0)
            {
                RenameFiles(moved.ToArray(), renamed.ToArray());
            }

            if (removed.Count > 0)
            {
                try
                {
                    BeginUpdate();
                    Cache(Hg.RemoveFiles(removed.ToArray()));
                }
                finally
                {
                    EndUpdate();
                }
            }
        }

        public void RenameFiles(string[] oldFileNames, string[] newFileNames)
        {
            var oldNames = new List<string>();
            var newNames = new List<string>();

            lock (_cache)
            {
                for (int i = 0; i < oldFileNames.Length; i++)
                {
                    var oldName = oldFileNames[i];
                    var newName = newFileNames[i];

                    if (newName.EndsWith("\\") || oldName.ToLower() == newName.ToLower())
                    {
                        continue;
                    }
                 
                    oldNames.Add(oldName);
                    newNames.Add(newName);
                    _cache.Remove(oldName);
                    _cache.Remove(newName);
                }
            }

            if (oldNames.Count > 0)
            {
                BeginUpdate();
                Hg.RenameFiles(oldNames.ToArray(), newNames.ToArray());
                EndUpdate();
            }
        }

        public void UpdateFileStatus(string[] fileNames)
        {
            Cache(Hg.GetFileStatus(fileNames));
        }

        public void UpdateRootStatus(string path)
        {
            Cache(Hg.GetRootStatus(path));
        }


        private string[] FilterIgnored(string[] fileNames)
        {
            var filtered = new List<string>();

            lock (_cache)
            {
                foreach (var fileName in fileNames)
                {
                    HgFileInfo fileInfo = null;
                    
                    _cache.TryGetValue(fileName.ToLower(), out fileInfo);

                    if (fileInfo != null && fileInfo.Status != HgFileStatus.Ignored)
                    {
                        filtered.Add(fileName);
                    }
                }
            }

            return filtered.ToArray();
        }


        public string GetBranchNames()
        {
            lock (_roots)
            {
                return _roots.Count > 0 ? _roots.Values.Distinct().Aggregate((x, y) => String.Concat(x, ", ", y)) : "";
            }
        }

        public string GetDirectoryBranch(string directory)
        {
            var branch = "";

            _roots.TryGetValue(directory, out branch);
            
            return branch;
        }

        public bool GetFileInfo(string fileName, out HgFileInfo info)
        {
            return _cache.TryGetValue(fileName, out info);
        }

        public HgFileStatus GetFileStatus(string fileName)
        {
            bool found = false;
            HgFileInfo value;

            lock (_cache)
            {
                found = _cache.TryGetValue(fileName, out value);
            }

            return (found ? value.Status : HgFileStatus.Uncontrolled);
        }

        public HgFileInfo[] GetPendingFiles()
        {
            lock (_cache)
            {
                return _cache.GetPendingFiles();
            }
        }


        public void ClearCache()
        {
            lock (_directoryWatchers.SyncRoot)
            {
                _directoryWatchers.UnsubscribeEvents();
                _directoryWatchers.Clear();
            }

            lock (_roots)
            {
                _roots.Clear();
            }

            lock (_cache)
            {
                _cache.Clear();
            }
        }

        
        private void BeginUpdate()
        {
            _updating = true;
        }

        private void EndUpdate()
        {
            _updating = false;
        }


        private void Cache(Dictionary<string, char> status)
        {
            if (status != null)
            {
                _cache.Add(status);
            }
        }

        private void UpdateCache()
        {
            try
            {
                BeginUpdate();

                _cacheUpdateRequired = false;
                
                _cache.Clear();
                _directoryWatchers.DumpDirtyFiles();

                foreach (var root in GetRoots().Where(x => !String.IsNullOrEmpty(x)))
                {
                    Cache(Hg.GetRootStatus(root));
                    _roots[root] = Hg.GetCurrentBranchName(root);
                }
            }
            finally
            {
                EndUpdate();
            }
        }

        private List<string> GetRoots()
        {
            List<string> roots = null;
            
            lock (_roots)
            {
                roots = new List<string>(_roots.Keys);
            }

            return roots;
        }
                

        private void OnTimerElapsed(object source, ElapsedEventArgs e)
        {
            var commands = _commands.DumpCommands();
            
            if (commands.Count > 0)
            {
                RunCommands(commands);
            }
            else
            {
                Update();
            }

            _timer.Start();
        }

        private void RunCommands(HgCommandQueue commands)
        {
            var dirtyFilesList = new List<string>();

            foreach (var command in commands)
            {
                command.Run(this, dirtyFilesList);
            }

            if (dirtyFilesList.Count > 0)
            {
                lock (_cache)
                {
                    Cache(Hg.GetFileStatus(dirtyFilesList.ToArray()));
                }
            }

            OnStatusChanged();
        }

        protected virtual void Update()
        {
            long numberOfChangedFiles = 0;
            double elapsed = 0;

            lock (_directoryWatchers.SyncRoot)
            {
                numberOfChangedFiles = _directoryWatchers.GetNumberOfChangedFiles();
                elapsed = (DateTime.Now - _directoryWatchers.GetLatestChange()).TotalMilliseconds;
            }

            if (elapsed < UpdateInterval)
            {
                return;
            }

            if (_cacheUpdateRequired || numberOfChangedFiles > 200)
            {
                UpdateCache();
                OnStatusChanged();
            }
            else if (numberOfChangedFiles > 0)
            {
                var dirtyFiles = PrepareDirtyFiles();

                if (_cacheUpdateRequired || dirtyFiles.Length == 0)
                {
                    return;
                }

                try
                {
                    BeginUpdate();
                    Cache(Hg.GetFileStatus(dirtyFiles));
                }
                finally
                {
                    EndUpdate();
                }
                
                OnStatusChanged(dirtyFiles);
            }
        }

        private string[] PrepareDirtyFiles()
        {
            var dirtyFiles = new List<string>();

            foreach (var dirtyFile in _directoryWatchers.DumpDirtyFiles())
            {
                if (!PrepareDirtyFile(dirtyFile))
                {
                    continue;
                }
   
                if (_cacheUpdateRequired) // Can be set by PrepareWatchedFile
                {
                    break;
                }

                dirtyFiles.Add(dirtyFile);
            }

            return dirtyFiles.ToArray();
        }

        private bool PrepareDirtyFile(string fileName)
        {
            if (Hg.IsDirectory(fileName))
            {
                return false;
            }
            
            if (fileName.IndexOf("\\.hg") != -1)
            {
                return false;
            }
            
            if (fileName.IndexOf(".hg\\dirstate") != -1)
            {
                _cacheUpdateRequired = !_updating;

                return false;
            }
            
            return IsDirty(fileName);
        }

        private bool IsDirty(string fileName)
        {
            var fileInfo = GetFileInfo(fileName);

            if (fileInfo == null)
            {
                return true;
            }
            
            return fileInfo.HasChanged;
        }

        private HgFileInfo GetFileInfo(string fileName)
        {
            HgFileInfo fileInfo = null;

            lock (_cache)
            {
                _cache.TryGetValue(fileName, out fileInfo);
            }

            return fileInfo;
        }


        protected virtual void OnStatusChanged(string[] dirtyFiles)
        {
            OnStatusChanged();
        }

        protected virtual void OnStatusChanged()
        {
            StatusChanged(this, EventArgs.Empty);
        }
    }
}