import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { App } from './App';
import { ToastProvider } from './hcm/components/ui/toast';
import { TooltipProvider } from './hcm/components/ui/tooltip';
import './styles.css';

const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false, retry: false } },
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <ToastProvider>
          <App />
        </ToastProvider>
      </TooltipProvider>
    </QueryClientProvider>
  </StrictMode>,
);