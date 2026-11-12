/**
 * A full-page POST to one of the server's account actions: `/Account/SignOut` and `/Account/ConnectTeams`.
 *
 * Those MVC actions refuse a GET, and refuse a POST another site sent (#853): when they took a GET, a link on any
 * page the admin visited could sign them out, or run the Teams connection, which Entra ID completes without a
 * prompt once consent has been granted. A form submitted from this page carries this origin, which is what the
 * server checks. It is a navigation, not an API call: the server takes the browser through Entra ID and back.
 */
export function postToServer(path: string): void {
  const form = document.createElement('form');
  form.method = 'post';
  form.action = path;
  form.style.display = 'none';
  document.body.appendChild(form);
  form.submit();
}
