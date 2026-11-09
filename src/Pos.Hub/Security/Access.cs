using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pos.Domain.Staff;
using Pos.Hub.Data;

namespace Pos.Hub.Security;

/// <summary>What kind of screen a device is.</summary>
public enum DeviceType
{
    /// <summary>A till.</summary>
    Till,

    /// <summary>A kitchen screen.</summary>
    KitchenScreen,

    /// <summary>A customer-facing display.</summary>
    CustomerDisplay,

    /// <summary>A back-office PC.</summary>
    BackOffice,
}

/// <summary>A staff member.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name shown on screens and receipts.</param>
/// <param name="Role">Role.</param>
/// <param name="IsActive">False once removed.</param>
public sealed record StaffMember(Guid Id, string Name, StaffRole Role, bool IsActive)
{
    /// <summary>True when the staff member's role includes the permission.</summary>
    public bool Can(Permission permission) => IsActive && RolePermissions.Has(Role, permission);
}

/// <summary>One change to the staff list.</summary>
/// <param name="At">When.</param>
/// <param name="StaffId">Who was changed.</param>
/// <param name="StaffName">Their name now.</param>
/// <param name="ChangedBy">Who made the change ("Set-up" for the first owner).</param>
/// <param name="Change">What changed (never a PIN).</param>
public sealed record StaffChange(DateTimeOffset At, Guid StaffId, string StaffName, string ChangedBy, string Change);

/// <summary>A registered till or screen.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name, e.g. "Till 1".</param>
/// <param name="Type">Kind of device.</param>
/// <param name="IsActive">False once removed.</param>
public sealed record DeviceRecord(Guid Id, string Name, DeviceType Type, bool IsActive);

/// <summary>Credentials given to a device once, when it is registered. The key is never shown again.</summary>
/// <param name="DeviceId">Device id.</param>
/// <param name="DeviceKey">Secret the device sends with every request.</param>
public sealed record PairedDevice(Guid DeviceId, string DeviceKey);

/// <summary>A staff login on a device.</summary>
/// <param name="Token">Session token for the Authorization header.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="Staff">Who logged in.</param>
public sealed record LoginResult(string Token, DateTimeOffset ExpiresAt, StaffMember Staff);

/// <summary>Who is calling the hub: always a registered device, and usually a logged-in staff member.</summary>
/// <param name="Device">The device.</param>
/// <param name="Staff">The staff member, or null for device-only calls (e.g. a customer display).</param>
public sealed record HubCaller(DeviceRecord Device, StaffMember? Staff);

/// <summary>A refused request: not registered, not logged in, not allowed, or locked out.</summary>
public sealed class AccessDeniedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AccessDeniedException()
        : this("Forbidden", "Not allowed.", 403)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AccessDeniedException(string message)
        : this("Forbidden", message, 403)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "Forbidden";
        StatusCode = 403;
    }

    /// <summary>Creates the exception with a code and HTTP status.</summary>
    public AccessDeniedException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>Stable code for the screen.</summary>
    public string Code { get; }

    /// <summary>HTTP status to return.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// Staff, devices and logins.
/// PINs are short, so their safety comes from lockout (5 wrong tries on a device locks PIN entry for 5 minutes),
/// not from slow hashing. They are stored as a keyed hash; the key lives in a separate file, not in the database.
/// Device keys and session tokens are long random secrets; only their hashes are stored.
/// </summary>
public sealed class AccessService
{
    /// <summary>Wrong PINs allowed on one device within <see cref="LockoutWindow"/>.</summary>
    public const int MaxPinFailures = 5;

    /// <summary>How long a staff login lasts.</summary>
    public static readonly TimeSpan SessionLength = TimeSpan.FromHours(12);

    /// <summary>Window for counting wrong PINs, and length of the lockout.</summary>
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);

    /// <summary>How long a till pairing code works.</summary>
    public static readonly TimeSpan PairingCodeLife = TimeSpan.FromMinutes(10);

    private readonly HubDatabase _db;
    private readonly TimeProvider _clock;
    private readonly byte[] _pinKey;

    /// <summary>Creates the service and loads (or creates) the PIN key file.</summary>
    public AccessService(HubDatabase db, HubSettings settings, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _db = db;
        _clock = clock;
        _pinKey = LoadOrCreateKey(settings.KeyFilePath);
    }

    // ---------- Staff ----------

    /// <summary>True once the first owner exists.</summary>
    public bool HasStaff => _db.Run(() => Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM staff;"), CultureInfo.InvariantCulture) > 0);

    /// <summary>Creates the first owner. Only works while there are no staff at all.</summary>
    public StaffMember CreateFirstOwner(string name, string pin) => _db.RunInTransaction(() =>
    {
        if (HasStaff)
        {
            throw new AccessDeniedException("AlreadySetUp", "The owner has already been set up.", 409);
        }

        var owner = Insert(name, StaffRole.Owner, pin);
        Record(null, owner.Id, "Shop set up: owner");
        return owner;
    });

    /// <summary>Adds a staff member.</summary>
    public StaffMember AddStaff(StaffMember actor, string name, StaffRole role, string pin) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!RolePermissions.CanManage(actor.Role, role))
        {
            throw new AccessDeniedException("Forbidden", $"A {actor.Role} cannot add a {role}.", 403);
        }

        var staff = Insert(name, role, pin);
        Record(actor.Id, staff.Id, $"Added as {role}");
        return staff;
    });

    /// <summary>
    /// Changes a staff member's PIN. Anyone may change their own (with their current PIN); managers and owners may set
    /// a new PIN for others they manage (for a forgotten PIN). The person's logins end, so they log in with the new PIN.
    /// </summary>
    public void ChangePin(StaffMember actor, Guid deviceId, Guid staffId, string pin, string? currentPin) => _db.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = FindStaffNow(staffId) ?? throw new KeyNotFoundException("Staff member not found.");
        if (actor.Id == staffId)
        {
            // The current PIN proves it is really them, not someone using a till left logged in. Wrong ones count
            // towards the lockout, like a wrong login.
            var who = _db.RunInTransaction(() => CheckPin(deviceId, currentPin ?? string.Empty));
            if (who?.Id != actor.Id)
            {
                if (who is not null)
                {
                    _db.Execute("INSERT INTO pin_failures (device_id, at) VALUES ($d, $at);", ("$d", deviceId.ToString()), ("$at", Iso(_clock.GetUtcNow())));
                }

                throw new AccessDeniedException("WrongPin", "Your current PIN is wrong.", 401);
            }
        }
        else if (!RolePermissions.CanManage(actor.Role, target.Role))
        {
            throw new AccessDeniedException("Forbidden", "Not allowed to change this PIN.", 403);
        }

        _db.RunInTransaction(() =>
        {
            if (!target.IsActive)
            {
                throw new InvalidOperationException($"{target.Name} has been removed. Bring them back first.");
            }

            ValidatePin(pin);
            EnsurePinFree(pin, staffId);
            _db.Execute("UPDATE staff SET pin_lookup = $p WHERE id = $id;", ("$p", PinLookup(pin)), ("$id", staffId.ToString()));
            _db.Execute("UPDATE sessions SET revoked = 1 WHERE staff_id = $id;", ("$id", staffId.ToString()));
            Record(actor.Id, staffId, actor.Id == staffId ? "Changed their own PIN" : "PIN reset");
            return 0;
        });
        return 0;
    });

    /// <summary>
    /// Changes a staff member's name or role. Nobody can change their own role (so the shop always keeps an owner,
    /// and nobody promotes themselves). A role change ends their logins, so new permissions apply at once.
    /// </summary>
    public StaffMember UpdateStaff(StaffMember actor, Guid staffId, string name, StaffRole role) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = FindStaff(staffId) ?? throw new KeyNotFoundException("Staff member not found.");
        if (actor.Id == staffId && role != target.Role)
        {
            throw new AccessDeniedException("Forbidden", "You cannot change your own role. Ask the owner.", 403);
        }

        if (actor.Id != staffId && (!RolePermissions.CanManage(actor.Role, target.Role) || !RolePermissions.CanManage(actor.Role, role)))
        {
            throw new AccessDeniedException("Forbidden", $"A {actor.Role} cannot make this change.", 403);
        }

        var clean = CleanName(name);
        EnsureNameFree(clean, staffId);
        var changes = new List<string>();
        if (clean != target.Name)
        {
            changes.Add($"Name {target.Name} → {clean}");
        }

        if (role != target.Role)
        {
            changes.Add($"Role {target.Role} → {role}");
            _db.Execute("UPDATE sessions SET revoked = 1 WHERE staff_id = $id;", ("$id", staffId.ToString()));
        }

        if (changes.Count > 0)
        {
            _db.Execute("UPDATE staff SET name = $n, role = $r WHERE id = $id;", ("$n", clean), ("$r", role.ToString()), ("$id", staffId.ToString()));
            Record(actor.Id, staffId, string.Join("; ", changes));
        }

        return target with { Name = clean, Role = role };
    });

    /// <summary>Removes a staff member: they can no longer log in, and their logins end now. Their history is kept.</summary>
    public void Deactivate(StaffMember actor, Guid staffId) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = FindStaff(staffId) ?? throw new KeyNotFoundException("Staff member not found.");
        if (actor.Id == staffId)
        {
            throw new AccessDeniedException("Forbidden", "You cannot remove yourself.", 403);
        }

        if (!RolePermissions.CanManage(actor.Role, target.Role))
        {
            throw new AccessDeniedException("Forbidden", "Not allowed to remove this staff member.", 403);
        }

        if (!target.IsActive)
        {
            return 0;
        }

        _db.Execute("UPDATE staff SET is_active = 0, pin_lookup = $dead WHERE id = $id;", ("$dead", "removed:" + staffId), ("$id", staffId.ToString()));
        _db.Execute("UPDATE sessions SET revoked = 1 WHERE staff_id = $id;", ("$id", staffId.ToString()));
        Record(actor.Id, staffId, "Removed");
        return 0;
    });

    /// <summary>Brings back a removed staff member, with a new PIN (their old one was cleared when they were removed).</summary>
    public StaffMember Reactivate(StaffMember actor, Guid staffId, string pin) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = FindStaff(staffId) ?? throw new KeyNotFoundException("Staff member not found.");
        if (!RolePermissions.CanManage(actor.Role, target.Role))
        {
            throw new AccessDeniedException("Forbidden", "Not allowed to bring back this staff member.", 403);
        }

        if (target.IsActive)
        {
            throw new InvalidOperationException($"{target.Name} is already active.");
        }

        EnsureNameFree(target.Name, staffId);
        ValidatePin(pin);
        EnsurePinFree(pin, staffId);
        _db.Execute("UPDATE staff SET is_active = 1, pin_lookup = $p WHERE id = $id;", ("$p", PinLookup(pin)), ("$id", staffId.ToString()));
        Record(actor.Id, staffId, "Brought back");
        return target with { IsActive = true };
    });

    /// <summary>A staff member's name, or "Unknown".</summary>
    public string StaffName(Guid staffId) => staffId == Online.OnlineOrderService.WebsiteUserId ? "Website" : _db.Run(() =>
        _db.Scalar("SELECT name FROM staff WHERE id = $id;", ("$id", staffId.ToString())) as string ?? "Unknown");

    /// <summary>All staff, active first, by name.</summary>
    public IReadOnlyList<StaffMember> ListStaff() => _db.Run(() =>
    {
        using var command = _db.Command("SELECT id, name, role, is_active FROM staff ORDER BY is_active DESC, name COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<StaffMember>();
        while (reader.Read())
        {
            result.Add(ReadStaff(reader));
        }

        return result;
    });

    /// <summary>Staff changes, newest first: who changed whom, and what (never the PINs themselves).</summary>
    public IReadOnlyList<StaffChange> StaffHistory(Guid? staffId, int limit = 200) => _db.Run(() =>
    {
        using var command = _db.Command(
            """
            SELECT h.at, h.staff_id, s.name, h.actor_id, a.name, h.change
            FROM staff_history h JOIN staff s ON s.id = h.staff_id LEFT JOIN staff a ON a.id = h.actor_id
            WHERE $s IS NULL OR h.staff_id = $s
            ORDER BY h.id DESC LIMIT $limit;
            """,
            ("$s", (object?)staffId?.ToString() ?? DBNull.Value), ("$limit", Math.Clamp(limit, 1, 1000)));
        using var reader = command.ExecuteReader();
        var result = new List<StaffChange>();
        while (reader.Read())
        {
            result.Add(new StaffChange(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(4) ? "Set-up" : reader.GetString(4), reader.GetString(5)));
        }

        return result;
    });

    // ---------- Devices ----------

    /// <summary>Creates a 6-digit code, valid for 10 minutes, to register one new till or screen.</summary>
    public string CreatePairingCode(StaffMember actor) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.Can(Permission.ManageDevices))
        {
            throw new AccessDeniedException("Forbidden", "Only the owner can register devices.", 403);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
        _db.Execute(
            "INSERT OR REPLACE INTO pairing_codes (code_hash, expires_at, created_by) VALUES ($h, $e, $by);",
            ("$h", Hash(code)), ("$e", Iso(_clock.GetUtcNow() + PairingCodeLife)), ("$by", actor.Id.ToString()));
        return code;
    });

    /// <summary>
    /// Registers a device with a pairing code. Returns its id and secret key, shown only this once.
    /// More than 10 wrong codes in 10 minutes blocks pairing for a while, so codes cannot be guessed.
    /// </summary>
    public PairedDevice Pair(string code, string name, DeviceType type) => _db.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var since = Iso(_clock.GetUtcNow() - PairingCodeLife);
        var failures = Convert.ToInt64(
            _db.Scalar("SELECT COUNT(*) FROM pin_failures WHERE device_id = 'pairing' AND at > $since;", ("$since", since)),
            CultureInfo.InvariantCulture);
        if (failures >= 10)
        {
            throw new AccessDeniedException("LockedOut", "Too many wrong codes. Try again in a few minutes.", 429);
        }

        var hash = Hash(code ?? string.Empty);
        var expires = _db.Scalar("SELECT expires_at FROM pairing_codes WHERE code_hash = $h;", ("$h", hash)) as string;
        if (expires is null || DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture) < _clock.GetUtcNow())
        {
            _db.Execute("INSERT INTO pin_failures (device_id, at) VALUES ('pairing', $at);", ("$at", Iso(_clock.GetUtcNow())));
            throw new AccessDeniedException("BadPairingCode", "That code is wrong or has expired.", 401);
        }

        return _db.RunInTransaction(() =>
        {
            _db.Execute("DELETE FROM pairing_codes WHERE code_hash = $h;", ("$h", hash));
            var id = Guid.CreateVersion7(_clock.GetUtcNow());
            var key = NewSecret();
            _db.Execute(
                "INSERT INTO devices (id, name, type, key_hash, is_active, created_at) VALUES ($id, $n, $t, $k, 1, $c);",
                ("$id", id.ToString()), ("$n", name.Trim()), ("$t", type.ToString()), ("$k", Hash(key)), ("$c", Iso(_clock.GetUtcNow())));
            return new PairedDevice(id, key);
        });
    });

    /// <summary>Registers the PC the owner is setting up from (first set-up only).</summary>
    public PairedDevice RegisterFirstDevice(string name) => _db.RunInTransaction(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = Guid.CreateVersion7(_clock.GetUtcNow());
        var key = NewSecret();
        _db.Execute(
            "INSERT INTO devices (id, name, type, key_hash, is_active, created_at) VALUES ($id, $n, $t, $k, 1, $c);",
            ("$id", id.ToString()), ("$n", name.Trim()), ("$t", DeviceType.BackOffice.ToString()), ("$k", Hash(key)), ("$c", Iso(_clock.GetUtcNow())));
        return new PairedDevice(id, key);
    });

    /// <summary>Checks a device's id and key; null if unknown, removed, or the key is wrong.</summary>
    public DeviceRecord? AuthenticateDevice(Guid deviceId, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return _db.Run(() =>
        {
            using var command = _db.Command("SELECT id, name, type, key_hash, is_active FROM devices WHERE id = $id;", ("$id", deviceId.ToString()));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(4) != 1 || !SameHash(reader.GetString(3), Hash(key)))
            {
                return null;
            }

            return new DeviceRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), Enum.Parse<DeviceType>(reader.GetString(2)), true);
        });
    }

    /// <summary>True when the device is registered and not removed.</summary>
    public bool IsDeviceActive(Guid deviceId) => _db.Run(() =>
        Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM devices WHERE id = $id AND is_active = 1;", ("$id", deviceId.ToString())), CultureInfo.InvariantCulture) == 1);

    /// <summary>Removes a device: it stops working immediately, and its logins end.</summary>
    public void RemoveDevice(StaffMember actor, Guid deviceId) => _db.RunInTransaction(() =>
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.Can(Permission.ManageDevices))
        {
            throw new AccessDeniedException("Forbidden", "Only the owner can remove devices.", 403);
        }

        if (_db.Execute("UPDATE devices SET is_active = 0 WHERE id = $id;", ("$id", deviceId.ToString())) == 0)
        {
            throw new KeyNotFoundException("Device not found.");
        }

        _db.Execute("UPDATE sessions SET revoked = 1 WHERE device_id = $id;", ("$id", deviceId.ToString()));
        return 0;
    });

    /// <summary>All registered devices.</summary>
    public IReadOnlyList<DeviceRecord> ListDevices() => _db.Run(() =>
    {
        using var command = _db.Command("SELECT id, name, type, is_active FROM devices ORDER BY created_at;");
        using var reader = command.ExecuteReader();
        var result = new List<DeviceRecord>();
        while (reader.Read())
        {
            result.Add(new DeviceRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), Enum.Parse<DeviceType>(reader.GetString(2)), reader.GetInt64(3) == 1));
        }

        return result;
    });

    // ---------- Logins ----------

    /// <summary>Logs a staff member in on a device with their PIN.</summary>
    public LoginResult Login(Guid deviceId, string pin) => _db.Run(() =>
    {
        // The PIN check is saved on its own, so a wrong PIN is still counted when the login is refused.
        var staff = _db.RunInTransaction(() => CheckPin(deviceId, pin)) ?? throw new AccessDeniedException("WrongPin", "Wrong PIN.", 401);
        return StartSession(staff, deviceId);
    });

    /// <summary>
    /// First-time set-up, all or nothing: creates the owner, registers the PC used for set-up, and logs the owner in on it.
    /// </summary>
    public (StaffMember Owner, PairedDevice Device, LoginResult Session) FirstTimeSetup(string ownerName, string pin, string deviceName) =>
        _db.RunInTransaction(() =>
        {
            var owner = CreateFirstOwner(ownerName, pin);
            var device = RegisterFirstDevice(deviceName);
            return (owner, device, StartSession(owner, device.DeviceId));
        });

    private LoginResult StartSession(StaffMember staff, Guid deviceId)
    {
        var token = NewSecret();
        var expires = _clock.GetUtcNow() + SessionLength;
        _db.Execute(
            "INSERT INTO sessions (token_hash, staff_id, device_id, expires_at) VALUES ($t, $s, $d, $e);",
            ("$t", Hash(token)), ("$s", staff.Id.ToString()), ("$d", deviceId.ToString()), ("$e", Iso(expires)));
        return new LoginResult(token, expires, staff);
    }

    /// <summary>The staff member for a session token on this device, or null if it is not valid.</summary>
    public StaffMember? AuthenticateSession(Guid deviceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return _db.Run(() =>
        {
            using var command = _db.Command(
                """
                SELECT s.id, s.name, s.role, s.is_active, x.expires_at FROM sessions x
                JOIN staff s ON s.id = x.staff_id
                WHERE x.token_hash = $t AND x.device_id = $d AND x.revoked = 0;
                """,
                ("$t", Hash(token)), ("$d", deviceId.ToString()));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture) <= _clock.GetUtcNow())
            {
                return null;
            }

            var staff = ReadStaff(reader);
            return staff.IsActive ? staff : null;
        });
    }

    /// <summary>Ends a session.</summary>
    public void Logout(string token) => _db.Run(() => _db.Execute("UPDATE sessions SET revoked = 1 WHERE token_hash = $t;", ("$t", Hash(token))));

    /// <summary>
    /// Checks a manager's PIN typed on this device to approve an action.
    /// Returns the approver if the PIN is right and their role allows the action; null if the role does not.
    /// A wrong PIN counts towards the lockout.
    /// </summary>
    public StaffMember? VerifyApprover(Guid deviceId, string pin, Permission permission) => _db.Run(() =>
    {
        var staff = _db.RunInTransaction(() => CheckPin(deviceId, pin)) ?? throw new AccessDeniedException("WrongPin", "Wrong manager PIN.", 401);
        return staff.Can(permission) ? staff : null;
    });

    // ---------- Internals ----------

    private StaffMember? CheckPin(Guid deviceId, string pin)
    {
        var since = Iso(_clock.GetUtcNow() - LockoutWindow);
        var failures = Convert.ToInt64(
            _db.Scalar("SELECT COUNT(*) FROM pin_failures WHERE device_id = $d AND at > $since;", ("$d", deviceId.ToString()), ("$since", since)),
            CultureInfo.InvariantCulture);
        if (failures >= MaxPinFailures)
        {
            throw new AccessDeniedException("LockedOut", "Too many wrong PINs. Try again in a few minutes.", 429);
        }

        StaffMember? staff = null;
        if (IsWellFormedPin(pin))
        {
            using var command = _db.Command("SELECT id, name, role, is_active FROM staff WHERE pin_lookup = $p AND is_active = 1;", ("$p", PinLookup(pin)));
            using var reader = command.ExecuteReader();
            staff = reader.Read() ? ReadStaff(reader) : null;
        }

        if (staff is null)
        {
            _db.Execute("INSERT INTO pin_failures (device_id, at) VALUES ($d, $at);", ("$d", deviceId.ToString()), ("$at", Iso(_clock.GetUtcNow())));
            return null;
        }

        _db.Execute("DELETE FROM pin_failures WHERE device_id = $d;", ("$d", deviceId.ToString()));
        return staff;
    }

    private StaffMember Insert(string name, StaffRole role, string pin)
    {
        var clean = CleanName(name);
        EnsureNameFree(clean, null);
        ValidatePin(pin);
        EnsurePinFree(pin, null);
        var staff = new StaffMember(Guid.CreateVersion7(_clock.GetUtcNow()), clean, role, true);
        _db.Execute(
            "INSERT INTO staff (id, name, role, pin_lookup, is_active, created_at) VALUES ($id, $n, $r, $p, 1, $c);",
            ("$id", staff.Id.ToString()), ("$n", staff.Name), ("$r", role.ToString()), ("$p", PinLookup(pin)), ("$c", Iso(_clock.GetUtcNow())));
        return staff;
    }

    /// <summary>Longest staff name (fits the till's top bar and printouts).</summary>
    public const int MaxNameLength = 40;

    private static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            throw new ArgumentException("Enter a name.");
        }

        if (clean.Length > MaxNameLength || clean.Any(char.IsControl))
        {
            throw new ArgumentException($"A name can be up to {MaxNameLength} characters.");
        }

        return clean;
    }

    // Two active staff with the same name would make reports and approvals ambiguous.
    private void EnsureNameFree(string name, Guid? exceptStaffId)
    {
        var other = _db.Scalar("SELECT id FROM staff WHERE is_active = 1 AND name = $n COLLATE NOCASE AND id <> $id;", ("$n", name), ("$id", exceptStaffId?.ToString() ?? string.Empty));
        if (other is not null)
        {
            throw new ArgumentException($"Someone called {name} is already on the staff list. Add a surname or initial.");
        }
    }

    private void Record(Guid? actorId, Guid staffId, string change) =>
        _db.Execute(
            "INSERT INTO staff_history (at, actor_id, staff_id, change) VALUES ($at, $a, $s, $c);",
            ("$at", Iso(_clock.GetUtcNow())), ("$a", (object?)actorId?.ToString() ?? DBNull.Value), ("$s", staffId.ToString()), ("$c", change));

    private StaffMember? FindStaffNow(Guid id) => _db.RunInTransaction(() => FindStaff(id));

    private void EnsurePinFree(string pin, Guid? exceptStaffId)
    {
        var owner = _db.Scalar("SELECT id FROM staff WHERE pin_lookup = $p;", ("$p", PinLookup(pin))) as string;
        if (owner is not null && owner != exceptStaffId?.ToString())
        {
            throw new ArgumentException("That PIN is already used by someone else. Choose another.");
        }
    }

    private StaffMember? FindStaff(Guid id)
    {
        using var command = _db.Command("SELECT id, name, role, is_active FROM staff WHERE id = $id;", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStaff(reader) : null;
    }

    private static StaffMember ReadStaff(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Enum.Parse<StaffRole>(reader.GetString(2)), reader.GetInt64(3) == 1);

    private static void ValidatePin(string pin)
    {
        if (!IsWellFormedPin(pin))
        {
            throw new ArgumentException("A PIN must be 4 to 8 digits.");
        }

        if (pin.Distinct().Count() == 1 || "0123456789".Contains(pin, StringComparison.Ordinal) || "9876543210".Contains(pin, StringComparison.Ordinal))
        {
            throw new ArgumentException("That PIN is too easy to guess (like 1111 or 1234). Choose another.");
        }
    }

    private static bool IsWellFormedPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    private string PinLookup(string pin) => Convert.ToHexString(HMACSHA256.HashData(_pinKey, Encoding.UTF8.GetBytes(pin)));

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool SameHash(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Reads the 32-byte hub key, creating it the first time.</summary>
    internal static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length != 32)
            {
                throw new InvalidOperationException($"The key file {path} is damaged.");
            }

            return existing;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        return key;
    }
}
