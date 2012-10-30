﻿using Kudu.Contracts.SourceControl;

namespace Kudu.Core.SourceControl
{
    public interface IServerRepository
    {
        bool Exists { get; }
        bool Initialize(RepositoryConfiguration configuration);
        ChangeSet Initialize(RepositoryConfiguration configuration, string path);
        RepositoryType GetRepositoryType();
        void Clean();
        void SetSSHEnv(string host, string homePath);
        void FetchWithoutConflict(string remoteUrl, string remoteAlias, string branchName);
        void SetReceiveInfo(string oldRef, string newRef, string branchName);

        void Update();
        void Commit(string message, string authorName);
    }
}
