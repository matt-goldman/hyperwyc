# Tutorial

This tutorial gives you a quick tour of the main features of Hyperwyc over five short pages. You will build a console app that keeps working when its API goes away, and at the end you will have seen every part of Hyperwyc do its job.

Everything here runs on a desktop with two terminals. There is no mobile device, no emulator and no network to unplug: you stop the API, which is the same thing as far as your client is concerned.

|                                                                 |                                                                                      |
| --------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| **[1. A read that works offline](1-reads.md)**                  | Watch a `GET` throw with the API stopped, add two lines, watch the same call survive |
| **[2. A write that survives](2-writes.md)**                     | `POST` with the API down, and read the `202`                                         |
| **[3. Finding out what happened](3-outcomes.md)**               | Start the API, flush the queue, and see the outcome arrive                           |
| **[4. Telling Hyperwyc about your network](4-connectivity.md)** | What the default costs you, and when to replace it                                   |
| **[5. Varying it per route](5-policies.md)**                    | TTL, and the route that must never be queued                                         |

**If you only want the five lines of setup**, read [Getting started](../getting-started.md) instead — it is the same registration without the narrative. **If you are building a .NET MAUI app**, [Hyperwyc in a .NET MAUI app](../maui.md) is the page you want.

Start with **[1. A read that works offline →](1-reads.md)**
