# Issue 73 - Review Accessibility Modifiers

To be fleshed out but capturing.

While implementing #23 I noted that `QueuedWrite` is `public`. It needs to be in order to enable people to write their own store implementation, but it prompted me to think we should audit types and members to ensure all accessibility modifiers are appropriate (i.e. not overly permissive or restrictive).