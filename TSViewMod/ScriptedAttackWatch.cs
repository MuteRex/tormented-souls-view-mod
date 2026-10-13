using UnityEngine;

namespace TSViewMod
{
    // True while a grab or death plays (and briefly after): those are animated for the fixed cameras.
    public class ScriptedAttackWatch
    {
        private Animator m_playerAnimator;
        private int m_animatorFlags = -1; // bit 1: has "Grabbed", bit 2: has "Dead"
        private float m_scriptedUntil;

        public bool Active(PlayerController player, bool enabled)
        {
            if (!enabled || player == null)
            {
                return false;
            }
            Animator animator = player.GetAnimator();
            if (animator != m_playerAnimator)
            {
                m_playerAnimator = animator;
                m_animatorFlags = -1;
            }
            if (animator == null || !animator.isActiveAndEnabled)
            {
                return false;
            }
            if (m_animatorFlags < 0)
            {
                m_animatorFlags = 0;
                foreach (AnimatorControllerParameter parameter in animator.parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Bool)
                    {
                        m_animatorFlags |= parameter.name == "Grabbed" ? 1 : parameter.name == "Dead" ? 2 : 0;
                    }
                }
            }
            bool scripted = (m_animatorFlags & 1) != 0 && animator.GetBool("Grabbed")
                || (m_animatorFlags & 2) != 0 && animator.GetBool("Dead")
                || animator.GetCurrentAnimatorStateInfo(0).IsName("Grabbed");
            if (scripted)
            {
                m_scriptedUntil = Time.unscaledTime + 0.8f;
            }
            return Time.unscaledTime < m_scriptedUntil;
        }
    }
}
