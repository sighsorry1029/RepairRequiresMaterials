using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RepairRequiresMaterials;

internal enum IncineratorDismantleResponse
{
    Failed,
    Success,
    NoEligibleItems,
    NoRoom,
    Busy,
    NoAccess,
    InventoryChanged
}

internal sealed class IncineratorDismantleController : MonoBehaviour
{
    private const string RequestRpc = RepairRequiresMaterialsPlugin.ModGuid + ".RequestIncineratorDismantle";
    private const string ResponseRpc = RepairRequiresMaterialsPlugin.ModGuid + ".IncineratorDismantleResponse";
    private const int RequestSchemaVersion = 1;
    private const int MaxKnownRecipeCount = 4096;
    private const int RequestHeaderBytes = sizeof(int) + sizeof(long) + sizeof(int);
    private const int RecipeTokenBytes = sizeof(ulong) * 2;
    private const int MaxRequestBytes = RequestHeaderBytes + MaxKnownRecipeCount * RecipeTokenBytes;

    private Incinerator? _incinerator;
    private ZNetView? _nview;
    private bool _registered;
    private bool _operationInProgress;

    private void Start()
    {
        TryRegisterRpcs();
    }

    private void OnDisable()
    {
        ReleaseOperation();
    }

    internal void Initialize(Incinerator incinerator)
    {
        _incinerator = incinerator;
        _nview = incinerator.GetComponent<ZNetView>();

        TryRegisterRpcs();
    }

    internal bool RequestDismantle(Player player)
    {
        TryRegisterRpcs();
        if (_nview == null || !_nview.IsValid() || !_nview.HasOwner())
        {
            return false;
        }

        if (!TryBuildRequest(player, out ZPackage? request) || request == null)
        {
            return false;
        }

        _nview.InvokeRPC(RequestRpc, request);
        return true;
    }

    private void TryRegisterRpcs()
    {
        if (_registered || _nview == null || !_nview.IsValid())
        {
            return;
        }

        _nview.Register<ZPackage>(RequestRpc, RpcRequestDismantle);
        _nview.Register<int, int>(ResponseRpc, RpcDismantleResponse);
        _registered = true;
    }

    private void RpcRequestDismantle(long senderUid, ZPackage request)
    {
        if (_incinerator == null || _nview == null || !_nview.IsValid() || !_nview.IsOwner())
        {
            return;
        }

        if (!RepairRequiresMaterialsPlugin.EnableIncineratorDismantling.Value.IsOn())
        {
            SendResponse(senderUid, IncineratorDismantleResponse.Failed);
            return;
        }

        if (_operationInProgress
            || _incinerator.isInUse
            || _incinerator.m_container.IsInUse()
            || _incinerator.m_container.m_loading)
        {
            SendResponse(senderUid, IncineratorDismantleResponse.Busy);
            return;
        }

        if (!TryReadRequestHeader(request, out long playerId, out int knownRecipeCount))
        {
            SendResponse(senderUid, IncineratorDismantleResponse.Failed);
            return;
        }

        if (!TryValidateRequester(senderUid, playerId, requireProximity: true))
        {
            SendResponse(senderUid, IncineratorDismantleResponse.NoAccess);
            return;
        }

        if (!TryReadKnownRecipeTokens(
                request,
                knownRecipeCount,
                out HashSet<IncineratorKnownRecipeToken>? knownRecipeTokens)
            || knownRecipeTokens == null)
        {
            SendResponse(senderUid, IncineratorDismantleResponse.Failed);
            return;
        }

        _operationInProgress = true;
        _incinerator.isInUse = true;

        try
        {
            Inventory inventory = _incinerator.m_container.GetInventory();
            IncineratorDismantleRollSeed rollSeed = IncineratorDismantleRollSeed.CreateRandom();
            if (!TryBuildApplicablePlan(
                    inventory,
                    knownRecipeTokens,
                    rollSeed,
                    out IncineratorDismantlePlan? plan,
                    out IncineratorDismantleResponse failureResponse)
                || plan == null)
            {
                SendResponse(senderUid, failureResponse);
                ReleaseOperation();
                return;
            }

            byte[] fingerprint = IncineratorDismantleCostSystem.GetFingerprint(inventory);
            StartCoroutine(DismantleCoroutine(
                senderUid,
                playerId,
                fingerprint,
                knownRecipeTokens,
                rollSeed));
        }
        catch (Exception exception)
        {
            RepairRequiresMaterialsPlugin.Log.LogError(
                $"Could not start incinerator dismantling: {exception.GetType().Name}: {exception.Message}");
            SendResponse(senderUid, IncineratorDismantleResponse.Failed);
            ReleaseOperation();
        }
    }

    private IEnumerator DismantleCoroutine(
        long senderUid,
        long playerId,
        byte[] expectedFingerprint,
        HashSet<IncineratorKnownRecipeToken> knownRecipeTokens,
        IncineratorDismantleRollSeed rollSeed)
    {
        bool leverPulled = false;
        try
        {
            if (_incinerator == null || _nview == null)
            {
                yield break;
            }

            _nview.InvokeRPC(ZNetView.Everybody, "RPC_AnimateLever");
            leverPulled = true;
            _incinerator.m_leverEffects.Create(transform.position, transform.rotation);

            yield return new WaitForSeconds(
                UnityEngine.Random.Range(
                    _incinerator.m_effectDelayMin,
                    _incinerator.m_effectDelayMax));

            _nview.InvokeRPC(ZNetView.Everybody, "RPC_AnimateLeverReturn");
            leverPulled = false;

            if (!_operationInProgress
                || !TryValidateRequester(senderUid, playerId, requireProximity: false)
                || _incinerator.m_container.IsInUse()
                || _incinerator.m_container.m_loading)
            {
                SendResponse(senderUid, IncineratorDismantleResponse.NoAccess);
                yield break;
            }

            Inventory inventory = _incinerator.m_container.GetInventory();
            if (!expectedFingerprint.SequenceEqual(IncineratorDismantleCostSystem.GetFingerprint(inventory)))
            {
                SendResponse(senderUid, IncineratorDismantleResponse.InventoryChanged);
                yield break;
            }

            if (!TryBuildApplicablePlan(
                    inventory,
                    knownRecipeTokens,
                    rollSeed,
                    out IncineratorDismantlePlan? plan,
                    out IncineratorDismantleResponse failureResponse)
                || plan == null)
            {
                SendResponse(senderUid, failureResponse);
                yield break;
            }

            if (!IncineratorDismantleCostSystem.TryApplyPlan(_incinerator.m_container, inventory, plan))
            {
                SendResponse(senderUid, IncineratorDismantleResponse.Failed);
                yield break;
            }

            try
            {
                if (_incinerator.m_lightingAOEs != null)
                {
                    Object.Instantiate(
                        _incinerator.m_lightingAOEs,
                        transform.position,
                        transform.rotation);
                }
            }
            catch (Exception exception)
            {
                RepairRequiresMaterialsPlugin.Log.LogWarning(
                    $"Could not create incinerator dismantle lighting effect: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }

            SendResponse(senderUid, IncineratorDismantleResponse.Success, plan.SourceUnitCount);
            yield return new WaitForSeconds(4f);
        }
        finally
        {
            if (leverPulled && _nview != null && _nview.IsValid())
            {
                _nview.InvokeRPC(ZNetView.Everybody, "RPC_AnimateLeverReturn");
            }

            ReleaseOperation();
        }
    }

    private static bool TryBuildApplicablePlan(
        Inventory inventory,
        HashSet<IncineratorKnownRecipeToken> knownRecipeTokens,
        IncineratorDismantleRollSeed rollSeed,
        out IncineratorDismantlePlan? plan,
        out IncineratorDismantleResponse failureResponse)
    {
        failureResponse = IncineratorDismantleResponse.NoEligibleItems;
        if (!IncineratorDismantleCostSystem.TryBuildPlan(
                inventory,
                knownRecipeTokens,
                rollSeed,
                out plan)
            || plan == null)
        {
            return false;
        }

        if (IncineratorDismantleCostSystem.CanApplyPlan(inventory, plan))
        {
            return true;
        }

        plan = null;
        failureResponse = IncineratorDismantleResponse.NoRoom;
        return false;
    }

    private bool TryValidateRequester(
        long senderUid,
        long playerId,
        bool requireProximity)
    {
        if (_incinerator == null
            || _nview == null
            || !_nview.IsValid()
            || !_nview.IsOwner()
            || !_incinerator.m_container.IsOwner()
            || playerId == 0L)
        {
            return false;
        }

        Player? requester = Player.GetPlayer(playerId);
        if (requester == null
            || requester.IsDead()
            || requester.m_nview == null
            || !requester.m_nview.IsValid()
            || requester.GetOwner() != senderUid)
        {
            return false;
        }

        if (requireProximity)
        {
            Transform leverTransform = _incinerator.m_incinerateSwitch != null
                ? _incinerator.m_incinerateSwitch.transform
                : transform;
            float maxDistance = Math.Max(0f, requester.m_maxInteractDistance) + 1f;
            if (Vector3.Distance(requester.GetEyePoint(), leverTransform.position) > maxDistance)
            {
                return false;
            }
        }

        if (!_incinerator.m_container.CheckAccess(playerId)
            || !HasWardAccess(transform.position, playerId))
        {
            return false;
        }

        return true;
    }

    private bool TryBuildRequest(Player player, out ZPackage? request)
    {
        request = null;
        if (player == null
            || player.GetPlayerID() == 0L
            || _incinerator == null
            || _incinerator.m_container == null
            || _incinerator.m_container.m_nview == null
            || !_incinerator.m_container.m_nview.IsValid())
        {
            return false;
        }

        HashSet<IncineratorKnownRecipeToken> tokens = new();
        // Container inventories are mirrored from their ZDO on every peer. Refresh
        // the local mirror, then disclose knowledge only for distinct eligible item
        // names currently visible in this incinerator instead of the character's
        // complete known-recipe set. A stale mirror can only cause an eligible item
        // to remain untouched; the owner still fingerprints and rebuilds the plan.
        _incinerator.m_container.CheckForChanges();
        foreach (ItemDrop.ItemData item in _incinerator.m_container.GetInventory().GetAllItems())
        {
            string? name = item?.m_shared?.m_name;
            if (IncineratorDismantleCostSystem.IsDismantleCandidate(item)
                && !string.IsNullOrEmpty(name)
                && player.IsRecipeKnown(name))
            {
                tokens.Add(IncineratorKnownRecipeToken.Create(name!));
            }
        }

        if (tokens.Count > MaxKnownRecipeCount)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Known-recipe dismantle request exceeded the {MaxKnownRecipeCount} recipe limit.");
            return false;
        }

        ZPackage package = new();
        package.Write(RequestSchemaVersion);
        package.Write(player.GetPlayerID());
        package.Write(tokens.Count);
        foreach (IncineratorKnownRecipeToken token in tokens
                     .OrderBy(value => value.First)
                     .ThenBy(value => value.Second))
        {
            package.Write(token.First);
            package.Write(token.Second);
        }

        if (package.Size() > MaxRequestBytes)
        {
            RepairRequiresMaterialsPlugin.Log.LogWarning(
                $"Known-recipe dismantle request exceeded the {MaxRequestBytes}-byte limit.");
            return false;
        }

        request = package;
        return true;
    }

    private static bool TryReadRequestHeader(
        ZPackage request,
        out long playerId,
        out int knownRecipeCount)
    {
        playerId = 0L;
        knownRecipeCount = 0;
        if (request == null || request.Size() <= 0 || request.Size() > MaxRequestBytes)
        {
            return false;
        }

        try
        {
            request.SetPos(0);
            if (request.ReadInt() != RequestSchemaVersion)
            {
                return false;
            }

            playerId = request.ReadLong();
            knownRecipeCount = request.ReadInt();
            if (playerId == 0L
                || knownRecipeCount < 0
                || knownRecipeCount > MaxKnownRecipeCount)
            {
                return false;
            }

            int expectedSize = RequestHeaderBytes + knownRecipeCount * RecipeTokenBytes;
            return request.Size() == expectedSize;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadKnownRecipeTokens(
        ZPackage request,
        int knownRecipeCount,
        out HashSet<IncineratorKnownRecipeToken>? knownRecipeTokens)
    {
        knownRecipeTokens = null;
        if (request == null
            || knownRecipeCount < 0
            || knownRecipeCount > MaxKnownRecipeCount
            || request.Size() != RequestHeaderBytes + knownRecipeCount * RecipeTokenBytes)
        {
            return false;
        }

        try
        {
            request.SetPos(RequestHeaderBytes);
            HashSet<IncineratorKnownRecipeToken> tokens = new();
            for (int index = 0; index < knownRecipeCount; ++index)
            {
                IncineratorKnownRecipeToken token = new(
                    request.ReadULong(),
                    request.ReadULong());
                if (!tokens.Add(token))
                {
                    return false;
                }
            }

            if (request.GetPos() != request.Size())
            {
                return false;
            }

            knownRecipeTokens = tokens;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasWardAccess(Vector3 position, long playerId)
    {
        bool allowedByOneWard = false;
        bool deniedByAnyWard = false;

        foreach (PrivateArea area in PrivateArea.m_allAreas.ToList())
        {
            if (area == null || !area.IsEnabled() || !area.IsInside(position, 0f))
            {
                continue;
            }

            bool permitted = area.m_piece != null
                && (area.m_piece.GetCreator() == playerId || area.IsPermitted(playerId));
            allowedByOneWard |= permitted;
            deniedByAnyWard |= !permitted;
        }

        return allowedByOneWard || !deniedByAnyWard;
    }

    private void SendResponse(
        long targetUid,
        IncineratorDismantleResponse response,
        int dismantledCount = 0)
    {
        if (_nview != null && _nview.IsValid())
        {
            _nview.InvokeRPC(targetUid, ResponseRpc, (int)response, dismantledCount);
        }
    }

    private static void RpcDismantleResponse(long senderUid, int responseValue, int dismantledCount)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        IncineratorDismantleResponse response = Enum.IsDefined(
            typeof(IncineratorDismantleResponse),
            responseValue)
            ? (IncineratorDismantleResponse)responseValue
            : IncineratorDismantleResponse.Failed;

        string message = response switch
        {
            IncineratorDismantleResponse.Success =>
                RepairRequiresMaterialsLocalization.Localize(
                    "$rrm_dismantle_success",
                    Math.Max(0, dismantledCount)),
            IncineratorDismantleResponse.NoEligibleItems =>
                "$rrm_dismantle_no_equipment",
            IncineratorDismantleResponse.NoRoom =>
                "$rrm_dismantle_no_room",
            IncineratorDismantleResponse.Busy =>
                "$rrm_dismantle_busy",
            IncineratorDismantleResponse.NoAccess =>
                "$rrm_dismantle_no_access",
            IncineratorDismantleResponse.InventoryChanged =>
                "$rrm_dismantle_inventory_changed",
            _ => "$rrm_dismantle_failed"
        };

        player.Message(MessageHud.MessageType.Center, message);
    }

    private void ReleaseOperation()
    {
        if (_operationInProgress && _incinerator != null)
        {
            _incinerator.isInUse = false;
        }

        _operationInProgress = false;
    }
}
