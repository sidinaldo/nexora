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
  //
  // ⚠️ `/api/cadastro/` ENTRA PELO SEGUNDO MOTIVO DESTA LISTA, e ele custa mais caro que o
  // primeiro. O `catchError` abaixo lê 401 como "a sessão venceu" e limpa tudo. A criação de
  // empresa responde 401 para chave ERRADA — indistinguível de chave ausente, de propósito —, e
  // isso é resposta de FORMULÁRIO, não de sessão. Sem esta linha o operador erra um caractere da
  // chave e o painel apaga a sessão dele, navega para /entrar e leva embora o formulário inteiro:
  // nome, CNPJ, e-mail e senha do cliente, redigitados do zero.
  //
  // PREFIXO, e não a rota inteira: tudo sob /api/cadastro/ acontece ANTES de existir sessão, e
  // uma rota nova ali esquecida nesta lista reintroduz o defeito inteiro.
  const ehPublico = req.url.includes('/auth/login')
    || req.url.includes('/api/convite/')
    || req.url.includes('/api/redefinir/')
    || req.url.includes('/api/cadastro/')
    || req.url.includes('/api/operador/');

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
