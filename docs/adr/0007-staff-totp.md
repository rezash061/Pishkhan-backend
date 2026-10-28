# 0007. Own RFC 6238 TOTP; mandatory for staff

Status: accepted (2026-10-28)

Staff accounts must enroll TOTP at first login (password + TOTP every login, 15-minute idle and 8-hour absolute session limits, step-up for sensitive actions, lockout after 5 failures). Users may opt in. TOTP is a 60-line RFC 6238 implementation (HMAC-SHA1, 30 s step, ±1 step, replay protection by last used step) to avoid a dependency for a small, well-specified algorithm; it is covered by the RFC test vectors.
