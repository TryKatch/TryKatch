namespace Trykatch.Infrastructure.Quotas;

/// <summary>Closed, inspectable trigger contracts for retained quota identities and terminal states.</summary>
internal static class QuotaPostgresTransitionContracts
{
    internal sealed record Contract(string Function, string Relation, string Trigger, string Body)
    {
        internal string Events { get; init; } = "UPDATE";
        internal string TriggerDefinition => $"CREATE TRIGGER {Trigger} BEFORE {Events} ON {Relation} FOR EACH ROW EXECUTE FUNCTION {Function}";
    }

    internal static IReadOnlyList<Contract> All { get; } =
    [
        new("app.guard_entitlement_identity()", "app.organization_entitlements", "guard_entitlement_identity", """
            BEGIN
              IF ROW(NEW."OrganizationId", NEW."ResourceKey", NEW."OwnerModuleId", NEW."Unit")
                 IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."ResourceKey", OLD."OwnerModuleId", OLD."Unit") THEN
                RAISE EXCEPTION 'Quota resource identity is immutable.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END
            """),
        new("app.guard_quota_reservation_transition()", "app.organization_quota_reservations", "guard_quota_reservation_transition", """
            BEGIN
              IF ROW(NEW."OrganizationId", NEW."Id", NEW."ResourceKey", NEW."ActorId", NEW."Units", NEW."OperationId",
                     NEW."EntitlementVersion", NEW."EffectKind", NEW."CreatedAtUtc", NEW."ExpiresAtUtc")
                 IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."Id", OLD."ResourceKey", OLD."ActorId", OLD."Units", OLD."OperationId",
                     OLD."EntitlementVersion", OLD."EffectKind", OLD."CreatedAtUtc", OLD."ExpiresAtUtc")
                 OR (OLD."State" IN (1, 2) AND ROW(NEW."State", NEW."ClosedAtUtc")
                     IS DISTINCT FROM ROW(OLD."State", OLD."ClosedAtUtc")) THEN
                RAISE EXCEPTION 'Quota reservation identity and closed state are immutable.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END
            """),
        new("app.guard_upload_transition()", "app.module_upload_operations", "guard_upload_transition", """
            BEGIN
              IF TG_OP = 'INSERT' THEN
                IF NEW."UploadPolicyKey" IS NULL OR NEW."UploadPolicyVersion" IS NULL THEN
                  RAISE EXCEPTION 'New uploads require an explicit policy identity.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
              END IF;
              IF ROW(NEW."OrganizationId", NEW."ResourceKey", NEW."OperationId", NEW."ActorId", NEW."MembershipId",
                     NEW."ReservationId", NEW."SubjectId", NEW."ObjectKey", NEW."StorageNamespaceFingerprint",
                     NEW."Length", NEW."Sha256", NEW."MetadataFingerprint", NEW."UploadPolicyKey", NEW."UploadPolicyVersion",
                     NEW."CreatedAtUtc", NEW."ExpiresAtUtc")
                 IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."ResourceKey", OLD."OperationId", OLD."ActorId", OLD."MembershipId",
                     OLD."ReservationId", OLD."SubjectId", OLD."ObjectKey", OLD."StorageNamespaceFingerprint",
                     OLD."Length", OLD."Sha256", OLD."MetadataFingerprint", OLD."UploadPolicyKey", OLD."UploadPolicyVersion",
                     OLD."CreatedAtUtc", OLD."ExpiresAtUtc")
                 OR (OLD."State" = 0 AND NEW."State" NOT IN (0, 1, 2))
                 OR (OLD."State" = 2 AND NEW."State" NOT IN (2, 3))
                 OR (OLD."State" IN (1, 3) AND ROW(NEW."State", NEW."ClosedAtUtc")
                     IS DISTINCT FROM ROW(OLD."State", OLD."ClosedAtUtc")) THEN
                RAISE EXCEPTION 'Upload identity and fenced or closed state cannot be reversed.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END
            """) { Events = "INSERT OR UPDATE" },
        new("infrastructure.guard_quota_maintenance_transition()", "infrastructure.quota_maintenance_work",
            "guard_quota_maintenance_transition", """
            BEGIN
              IF ROW(NEW."OrganizationId", NEW."Id", NEW."Kind", NEW."ResourceKey", NEW."ReservationId",
                     NEW."UploadOperationId", NEW."CreatedAtUtc")
                 IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."Id", OLD."Kind", OLD."ResourceKey", OLD."ReservationId",
                     OLD."UploadOperationId", OLD."CreatedAtUtc")
                 OR NEW."Attempts" < OLD."Attempts"
                 OR (OLD."State" = 3 AND NEW IS DISTINCT FROM OLD) THEN
                RAISE EXCEPTION 'Quota recovery references and completed work are immutable.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END
            """)
    ];
}
