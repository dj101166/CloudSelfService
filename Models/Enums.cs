namespace CloudSelfService.Models;

public enum UserKind { Staff, Customer }

public enum StaffRole { None = 0, Staff = 1, StaffAdmin = 2, Owner = 3 }

public enum AccessLevel { None = 0, Read = 1, Contribute = 2, Full = 3 }

public enum StorageKind { FileShare, BlobContainer }

public enum CustomerCloud { Commercial, AzureGov }

public enum CrossTenantStatus { NotRequired, Pending, Complete }

public enum StorageOperation { List, Download, Upload, Overwrite, CreateFolder, Move, Delete }

public enum AuditResult { Success, Denied, Failed }
