import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthServico } from '../servicos/auth.servico';
import { ThrottleLogin } from './throttle-login';

/** Anexa o token e trata as duas respostas que exigem reação global.
 *
 *  O Recupera tem aqui um ramo que escolhe entre token de tenant e token de plataforma pela
 *  URL — não há backoffice no Nexora, então o arquivo fica na metade do tamanho. */
export const interceptorToken: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthServico);
  const router = inject(Router);
  const throttle = inject(ThrottleLogin);

  // Os fluxos públicos (login, aceite de convite, redefinição) não levam token: o usuário
  // ainda não tem sessão, e mandar um token velho faria a API recusar por expiração.
  const ehPublico = req.url.includes('/auth/login')
    || req.url.includes('/api/convite/')
    || req.url.includes('/api/redefinir/');

  const token = ehPublico ? null : auth.token;
  const requisicao = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(requisicao).pipe(
    catchError((e: HttpErrorResponse) => {
      // Token expirado ou inválido: derruba a sessão e manda para o login.
      if (e.status === 401 && !ehPublico) {
        auth.limpar();
        router.navigate(['/entrar']);
      }

      // Rate limit no login: dispara a contagem regressiva do botão. A mensagem {erro} a
      // própria tela mostra.
      if (e.status === 429 && req.url.includes('/auth/login')) {
        throttle.iniciar(Number(e.headers.get('Retry-After')) || 60);
      }

      return throwError(() => e);
    })
  );
};
