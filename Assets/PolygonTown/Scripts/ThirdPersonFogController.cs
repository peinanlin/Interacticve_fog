using UnityEngine;

namespace PolygonTown.Demo
{
    [DisallowMultipleComponent]
    public sealed class ThirdPersonFogController : MonoBehaviour
    {
        public CharacterController characterController;
        public Animator animator;
        public float walkSpeed = 4.2f;
        public float sprintSpeed = 7f;
        public float jumpHeight = 1.35f;
        public float gravity = -22f;
        public float rotationSmoothTime = 0.08f;

        private float verticalVelocity;
        private float rotationVelocity;
        private int requestedAnimationState;

        private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle_gunMiddle_AR");
        private static readonly int RunState = Animator.StringToHash("Base Layer.Run_gunMiddle_AR");
        private static readonly int JumpState = Animator.StringToHash("Base Layer.Jump");

        private void Awake()
        {
            if (characterController == null)
                characterController = GetComponent<CharacterController>();
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        private void Update()
        {
            if (characterController == null)
                return;

            var input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            input = Vector2.ClampMagnitude(input, 1f);

            var cameraTransform = Camera.main != null ? Camera.main.transform : null;
            var forward = cameraTransform != null ? cameraTransform.forward : Vector3.forward;
            var right = cameraTransform != null ? cameraTransform.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();

            var movement = forward * input.y + right * input.x;
            if (movement.sqrMagnitude > 0.001f)
            {
                var targetAngle = Mathf.Atan2(movement.x, movement.z) * Mathf.Rad2Deg;
                var angle = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetAngle,
                    ref rotationVelocity, rotationSmoothTime);
                transform.rotation = Quaternion.Euler(0f, angle, 0f);
            }

            if (characterController.isGrounded && verticalVelocity < 0f)
                verticalVelocity = -2f;
            if (characterController.isGrounded && Input.GetButtonDown("Jump"))
                verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            verticalVelocity += gravity * Time.deltaTime;

            var speed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;
            var velocity = movement * speed + Vector3.up * verticalVelocity;
            characterController.Move(velocity * Time.deltaTime);

            UpdateAnimator(input.magnitude, characterController.isGrounded);
        }

        private void UpdateAnimator(float moveAmount, bool grounded)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return;

            if (animator.parameters.Length == 0)
            {
                UpdateStateBasedAnimator(moveAmount, grounded);
                return;
            }

            SetFloatIfPresent("Speed", moveAmount, 0.1f);
            SetBoolIfPresent("Grounded", grounded);
        }

        private void UpdateStateBasedAnimator(float moveAmount, bool grounded)
        {
            var desiredState = !grounded ? JumpState : moveAmount > 0.05f ? RunState : IdleState;
            if (!animator.HasState(0, desiredState))
                return;

            var currentState = animator.GetCurrentAnimatorStateInfo(0);
            var nextState = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0) : default;
            var alreadyPlaying = currentState.fullPathHash == desiredState || nextState.fullPathHash == desiredState;
            if (requestedAnimationState != desiredState || !alreadyPlaying)
                animator.CrossFadeInFixedTime(desiredState, 0.12f, 0);
            requestedAnimationState = desiredState;
        }

        private void SetFloatIfPresent(string parameterName, float value, float dampTime)
        {
            foreach (var parameter in animator.parameters)
            {
                if (parameter.name == parameterName && parameter.type == AnimatorControllerParameterType.Float)
                {
                    animator.SetFloat(parameterName, value, dampTime, Time.deltaTime);
                    return;
                }
            }
        }

        private void SetBoolIfPresent(string parameterName, bool value)
        {
            foreach (var parameter in animator.parameters)
            {
                if (parameter.name == parameterName && parameter.type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(parameterName, value);
                    return;
                }
            }
        }
    }
}
