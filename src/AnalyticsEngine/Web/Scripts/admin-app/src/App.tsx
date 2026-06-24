import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import HomePage from './pages/HomePage';
import TeamsPermissionsPage from './pages/TeamsPermissionsPage';
import UserLookupPage from './pages/UserLookupPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) =>
  'nav-link' + (isActive ? ' active' : '');

/**
 * App shell: top navigation + client-side routes. Uses HashRouter so the whole SPA is
 * served by a single MVC action (no IIS / MVC route changes needed to add pages).
 */
export default function App() {
  return (
    <>
      <nav className="navbar navbar-expand-lg navbar-dark bg-primary">
        <a className="navbar-brand" href="/">
          Microsoft 365 Advanced Analytics
        </a>
        <ul className="navbar-nav mr-auto">
          <li className="nav-item">
            <NavLink className={navLinkClass} to="/home">
              Home
            </NavLink>
          </li>
          <li className="nav-item">
            <NavLink className={navLinkClass} to="/teams">
              Teams Permissions
            </NavLink>
          </li>
          <li className="nav-item">
            <NavLink className={navLinkClass} to="/user-lookup">
              User Data Lookup
            </NavLink>
          </li>
        </ul>
        <ul className="navbar-nav ml-auto">
          <li className="nav-item">
            {/* Server-side OIDC sign-out (full page navigation, not a SPA route). */}
            <a className="nav-link" href="/Account/SignOut">
              Sign out
            </a>
          </li>
        </ul>
      </nav>

      <main className="aa-content">
        <Routes>
          <Route path="/" element={<Navigate to="/home" replace />} />
          <Route path="/home" element={<HomePage />} />
          <Route path="/teams" element={<TeamsPermissionsPage />} />
          <Route path="/user-lookup" element={<UserLookupPage />} />
          <Route path="*" element={<Navigate to="/home" replace />} />
        </Routes>
      </main>
    </>
  );
}
