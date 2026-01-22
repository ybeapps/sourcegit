using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RevisionCompare : ObservableObject, IDisposable
    {
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public object StartPoint
        {
            get => _startPoint;
            private set => SetProperty(ref _startPoint, value);
        }

        public object EndPoint
        {
            get => _endPoint;
            private set => SetProperty(ref _endPoint, value);
        }

        public bool CanSaveAsPatch { get; }

        public bool CanResetFiles => _repository != null && !_repository.IsBare;

        public int TotalChanges
        {
            get => _totalChanges;
            private set => SetProperty(ref _totalChanges, value);
        }

        public List<Models.Change> VisibleChanges
        {
            get => _visibleChanges;
            private set => SetProperty(ref _visibleChanges, value);
        }

        public List<Models.Change> SelectedChanges
        {
            get => _selectedChanges;
            set
            {
                if (SetProperty(ref _selectedChanges, value))
                {
                    if (value is { Count: 1 })
                    {
                        var option = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), value[0]);
                        DiffContext = new DiffContext(_repo, option, _diffContext);
                    }
                    else
                    {
                        DiffContext = null;
                    }
                }
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (SetProperty(ref _searchFilter, value))
                    RefreshVisible();
            }
        }

        public DiffContext DiffContext
        {
            get => _diffContext;
            private set => SetProperty(ref _diffContext, value);
        }

        public RevisionCompare(string repo, Models.Commit startPoint, Models.Commit endPoint)
            : this(repo, null, startPoint, endPoint)
        {
        }

        public RevisionCompare(string repo, Repository repository, Models.Commit startPoint, Models.Commit endPoint)
        {
            _repo = repo;
            _repository = repository;
            _startPoint = (object)startPoint ?? new Models.Null();
            _endPoint = (object)endPoint ?? new Models.Null();
            CanSaveAsPatch = startPoint != null && endPoint != null;
            Refresh();
        }

        public void Dispose()
        {
            _repo = null;
            _repository = null;
            _startPoint = null;
            _endPoint = null;
            _changes?.Clear();
            _visibleChanges?.Clear();
            _selectedChanges?.Clear();
            _searchFilter = null;
            _diffContext = null;
        }

        public void OpenChangeWithExternalDiffTool(Models.Change change)
        {
            var opt = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), change);
            new Commands.DiffTool(_repo, opt).Open();
        }

        public void NavigateTo(string commitSHA)
        {
            var launcher = App.GetLauncher();
            if (launcher == null)
                return;

            foreach (var page in launcher.Pages)
            {
                if (page.Data is Repository repo && repo.FullPath.Equals(_repo))
                {
                    repo.NavigateToCommit(commitSHA);
                    break;
                }
            }
        }

        public void Swap()
        {
            (StartPoint, EndPoint) = (_endPoint, _startPoint);
            VisibleChanges = [];
            SelectedChanges = [];
            IsLoading = true;
            Refresh();
        }

        public string GetAbsPath(string path)
        {
            return Native.OS.GetAbsPath(_repo, path);
        }

        public async Task SaveChangesAsPatchAsync(List<Models.Change> changes, string saveTo)
        {
            var succ = await Commands.SaveChangesAsPatch.ProcessRevisionCompareChangesAsync(_repo, changes ?? _changes, GetSHA(_startPoint), GetSHA(_endPoint), saveTo);
            if (succ)
                App.SendNotification(_repo, App.Text("SaveAsPatchSuccess"));
        }

        public async Task ResetToSourceRevisionAsync(string path)
        {
            var sourceSHA = GetSHA(_startPoint);
            if (string.IsNullOrEmpty(sourceSHA))
                return;

            var log = _repository?.CreateLog($"Reset File to '{sourceSHA}'");
            await new Commands.Checkout(_repo).Use(log).FileWithRevisionAsync(path, sourceSHA);
            log?.Complete();
        }

        public async Task ResetToTargetRevisionAsync(string path)
        {
            var targetSHA = GetSHA(_endPoint);
            if (string.IsNullOrEmpty(targetSHA))
                return;

            var log = _repository?.CreateLog($"Reset File to '{targetSHA}'");
            await new Commands.Checkout(_repo).Use(log).FileWithRevisionAsync(path, targetSHA);
            log?.Complete();
        }

        public async Task ResetMultipleToSourceRevisionAsync(List<Models.Change> changes)
        {
            var sourceSHA = GetSHA(_startPoint);
            if (string.IsNullOrEmpty(sourceSHA))
                return;

            var files = new List<string>();
            foreach (var c in changes)
                files.Add(c.Path);

            var log = _repository?.CreateLog($"Reset Files to '{sourceSHA}'");
            await new Commands.Checkout(_repo).Use(log).MultipleFilesWithRevisionAsync(files, sourceSHA);
            log?.Complete();
        }

        public async Task ResetMultipleToTargetRevisionAsync(List<Models.Change> changes)
        {
            var targetSHA = GetSHA(_endPoint);
            if (string.IsNullOrEmpty(targetSHA))
                return;

            var files = new List<string>();
            foreach (var c in changes)
                files.Add(c.Path);

            var log = _repository?.CreateLog($"Reset Files to '{targetSHA}'");
            await new Commands.Checkout(_repo).Use(log).MultipleFilesWithRevisionAsync(files, targetSHA);
            log?.Complete();
        }

        public void ClearSearchFilter()
        {
            SearchFilter = string.Empty;
        }

        private void RefreshVisible()
        {
            if (_changes == null)
                return;

            if (string.IsNullOrEmpty(_searchFilter))
            {
                VisibleChanges = _changes;
            }
            else
            {
                var visible = new List<Models.Change>();
                foreach (var c in _changes)
                {
                    if (c.Path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                        visible.Add(c);
                }

                VisibleChanges = visible;
            }
        }

        private void Refresh()
        {
            Task.Run(async () =>
            {
                _changes = await new Commands.CompareRevisions(_repo, GetSHA(_startPoint), GetSHA(_endPoint))
                    .ReadAsync()
                    .ConfigureAwait(false);

                var visible = _changes;
                if (!string.IsNullOrWhiteSpace(_searchFilter))
                {
                    visible = [];
                    foreach (var c in _changes)
                    {
                        if (c.Path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                            visible.Add(c);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    TotalChanges = _changes.Count;
                    VisibleChanges = visible;
                    IsLoading = false;

                    if (VisibleChanges.Count > 0)
                        SelectedChanges = [VisibleChanges[0]];
                    else
                        SelectedChanges = [];
                });
            });
        }

        private string GetSHA(object obj)
        {
            return obj is Models.Commit commit ? commit.SHA : string.Empty;
        }

        private string _repo;
        private Repository _repository = null;
        private bool _isLoading = true;
        private object _startPoint = null;
        private object _endPoint = null;
        private int _totalChanges = 0;
        private List<Models.Change> _changes = null;
        private List<Models.Change> _visibleChanges = null;
        private List<Models.Change> _selectedChanges = null;
        private string _searchFilter = string.Empty;
        private DiffContext _diffContext = null;
    }
}
