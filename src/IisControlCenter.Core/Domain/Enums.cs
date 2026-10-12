namespace IisControlCenter.Core.Domain;

public enum AgentStatus { NotRegistered, Pending, Online, Offline, Revoked }

public enum CertificateStatus { Active, Revoked, Superseded }

public enum PrincipalType { Group, User }

public enum RoleScope { Platform, Project }

public enum Role
{
    // platform
    PlatformAdmin,
    ServerOperator,
    Auditor,
    // project (used from Phase 2)
    ProjectViewer,
    Deployer,
    Approver,
    ProjectOwner,
}

public enum MappingStatus { Unassigned, Mapped, Missing }

public enum TargetStatus { Active, Drifted, Missing, Disabled }

public enum ProtectedPathKind { ConfigFile, DataFolder }

public enum LinkRequestStatus { Pending, Approved, Rejected, Cancelled }

public enum DriftKind { Created, Deleted, Changed }
