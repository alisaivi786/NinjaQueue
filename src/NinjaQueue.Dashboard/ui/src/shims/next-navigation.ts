// The copied screens navigate with Next.js's router; the dashboard routes in the URL hash instead.
export function useRouter() {
  return {
    push: (path: string) => {
      window.location.hash = path.startsWith('/') ? path : `/${path}`;
    },
    replace: (path: string) => {
      window.location.replace(`#${path.startsWith('/') ? path : `/${path}`}`);
    },
    back: () => window.history.back(),
  };
}