namespace Ta.Backend.Features.Identity;

public sealed class Account
{
    public Guid AccountId { get; set; } = Guid.NewGuid();
    public string AccountEmail { get; set; } = "";
    public string AccountPasswordHash { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountRole { get; set; } = Roles.Student;
    public string AccountStatus { get; set; } = "pending";
    public int AccountFailedLogins { get; set; }
    public DateTimeOffset? AccountLockedUntil { get; set; }
    public DateTimeOffset AccountCreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountSession
{
    public Guid AccountSessionId { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string AccountSessionTokenHash { get; set; } = "";
    public DateTimeOffset AccountSessionExpiresAt { get; set; }
    public bool AccountSessionRevoked { get; set; }
}

public static class Roles
{
    public const string Administrator = "administrator";
    public const string Lecturer = "lecturer";
    public const string Student = "student";
    public const string HumanScheme = "Human";
    public const string HumanPolicy = "Human";
    public const string StaffPolicy = "Staff";
    public const string StudentPolicy = "Student";
    public const string AdministratorPolicy = "AcademicAdministrator";
}
