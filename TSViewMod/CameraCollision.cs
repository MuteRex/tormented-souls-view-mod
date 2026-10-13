using System.Collections.Generic;
using UnityEngine;

namespace TSViewMod
{
    // Camera collision: physics first, then the room's walkable area (RoomBounds).
    public static class CameraCollision
    {
        // Colliders first, then the walkable area: many walls have no collider and only void behind them.
        public const float NavOverhang = 0.3f;

        public static Vector3 Constrain(Vector3 pivot, Vector3 desired, Transform player)
        {
            Vector3 position = ResolveCollision(pivot, desired, player);
            Vector3 arm = position - pivot;
            float length = arm.magnitude;
            if (length < 0.01f || !RoomBounds.Inside(pivot, NavOverhang))
            {
                return position;
            }
            const float step = 0.05f;
            float allowed = 0f;
            for (float d = step; d <= length + 1e-4f; d += step)
            {
                if (!RoomBounds.Inside(pivot + arm * (d / length), NavOverhang))
                {
                    break;
                }
                allowed = d;
            }
            allowed = Mathf.Min(allowed, length);
            return pivot + arm * (Mathf.Max(allowed, 0.25f) / length);
        }

        public static Vector3 ResolveCollision(Vector3 pivot, Vector3 desired, Transform player)
        {
            Vector3 dir = desired - pivot;
            float distance = dir.magnitude;
            if (distance < 0.001f)
            {
                return desired;
            }
            dir /= distance;
            const float radius = 0.18f;
            float best = distance;
            foreach (RaycastHit hit in Physics.SphereCastAll(pivot, radius, dir, distance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(player) || hit.collider.attachedRigidbody != null && !hit.collider.attachedRigidbody.isKinematic
                    || IgnoredByCamera(hit.collider) || WaistHigh(hit.collider, player.position.y))
                {
                    continue;
                }
                if (hit.distance < best)
                {
                    best = hit.distance;
                }
            }
            return pivot + dir * Mathf.Max(best - 0.05f, 0.25f);
        }

        // Small props and enemies don't push the camera; walls, big furniture, floors and ceilings do.
        private static readonly Dictionary<Collider, bool> s_ignored = new Dictionary<Collider, bool>();

        private static bool IgnoredByCamera(Collider collider)
        {
            if (s_ignored.TryGetValue(collider, out bool ignored))
            {
                return ignored;
            }
            if (s_ignored.Count > 4096)
            {
                s_ignored.Clear(); // old rooms' colliders
            }
            Vector3 size = collider.bounds.size;
            bool thin = size.x < 0.5f && size.z < 0.5f; // poles, legs, lamp stands
            bool small = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 1.2f; // boxes, chairs, carts
            ignored = thin || small || collider.GetComponentInParent<EnemyBrain>() != null;
            s_ignored[collider] = ignored;
            return ignored;
        }

        // Railings, tables and counters: look over them rather than pull into her head.
        private static bool WaistHigh(Collider collider, float feet)
        {
            Bounds b = collider.bounds;
            return b.size.y < 1.4f && b.max.y > feet + 0.3f && b.max.y < feet + 1.6f && b.min.y > feet - 0.6f;
        }
    }
}
