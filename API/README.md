# Neuro API Documentation

The Neuro API works by communicating between the game and Neuro using a websocket. The game needs to connect to a websocket server url, that should be configurable somehow, preferably through a file.

You can view the API specification [here](./SPECIFICATION.md).

For guidance on how to structure actions and context so Neuro plays your game well, see the [integration best practices](./BEST_PRACTICES.md).

Games with built-in voice chat can optionally use the [voice chat side-channel](./VOICE_CHAT.md) to let Neuro hear and talk to the other players through the game's voice chat.

## Additional Guide for Using Actions

Due to how action forces work, this system is very susceptible to race conditions. As such, you need to be careful with how they are handled so that you don't duplicate action messages or executions or enter a deadlock.

After actions are registered, Neuro might try using them at any time, even before you send an action force. If you send an action force at the same time as Neuro sends an action, you need to be able to handle that.

Below is a list of recommendations to follow when using actions.

### Actions should be listened for without taking into account the action force state.

Your action handler should be prepared to handle actions at any time, even if they are sent before an action force.

### Unregister disposable actions before sending result.

If you are using disposable actions (for example, a "play_card" action in a turn-based game), you should unregister them before sending back the action result. This will ensure that Neuro doesn't try to use it again.

If you have an upcoming actions force, you should cancel it when the disposable actions are unregistered. If it's too late for that however, don't worry since Neuro will ignore the force.

### Be careful with non-disposable actions

If you want to force an action that is non-disposable and can be used multiple times, please take into account that Neuro might decide to perform that action immediately before or immediately after the force is handled. You should be able to handle that in-game, or better yet, avoid using action forces with non-diposable actions.

---

The [ActionWindow.cs](../Unity/Assets/Actions/ActionWindow.cs) file in the Unity SDK is a very good example of how to handle actions without running into race conditions.