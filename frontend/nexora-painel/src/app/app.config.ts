import {
  ApplicationConfig,
  LOCALE_ID,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection
} from '@angular/core';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { registerLocaleData } from '@angular/common';
import ptBr from '@angular/common/locales/pt';

import { routes } from './app.routes';
import { interceptorToken } from './nucleo/seguranca/interceptor-token';

// Valores em BRL e datas em dd/MM/aaaa. Sem isto o CurrencyPipe formata em dólar e o
// DatePipe em MM/dd — erro que passa despercebido até um cliente reclamar da data.
registerLocaleData(ptBr);

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withInMemoryScrolling({ scrollPositionRestoration: 'enabled' })),
    provideHttpClient(withInterceptors([interceptorToken])),
    { provide: LOCALE_ID, useValue: 'pt-BR' }
  ]
};
