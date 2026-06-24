import { useEffect } from 'react';
import { Toaster, useToastController, useId, Toast, ToastTitle } from '@fluentui/react-components';
import type { ToastIntent } from '@fluentui/react-components';

// Module-level dispatcher, set by <AppToaster /> once it has mounted. This lets non-React code
// and class components raise toasts without needing the useToastController hook.
let dispatch: ((message: string, intent: ToastIntent) => void) | null = null;

/** Raise a Fluent toast. Safe to call from class components and plain functions. */
export function notify(message: string, intent: ToastIntent = 'info') {
  if (dispatch) {
    dispatch(message, intent);
  } else {
    // Toaster not mounted yet (very early in startup) - fall back to the console.
    console.log(`[toast:${intent}] ${message}`);
  }
}

export const notifySuccess = (message: string) => notify(message, 'success');
export const notifyError = (message: string) => notify(message, 'error');

/**
 * Default export mimicking the react-hot-toast API (`toast()`, `toast.success()`, `toast.error()`)
 * so call sites only need to change the import path.
 */
const toast = Object.assign((message: string) => notify(message, 'info'), {
  success: (message: string) => notify(message, 'success'),
  error: (message: string) => notify(message, 'error'),
});
export default toast;

/**
 * Renders the single app-wide Fluent Toaster and wires the module-level dispatcher. Render once,
 * inside the FluentProvider tree.
 */
export function AppToaster() {
  const toasterId = useId('app-toaster');
  const { dispatchToast } = useToastController(toasterId);

  useEffect(() => {
    dispatch = (message, intent) =>
      dispatchToast(
        <Toast>
          <ToastTitle>{message}</ToastTitle>
        </Toast>,
        { intent },
      );
    return () => {
      dispatch = null;
    };
  }, [dispatchToast]);

  return <Toaster toasterId={toasterId} position="top-end" />;
}
