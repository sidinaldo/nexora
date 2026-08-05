import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthServico } from '../servicos/auth.servico';

/** Sem sessão, não entra. */
export const guardaAutenticado: CanActivateFn = () => {
  const auth = inject(AuthServico);
  const router = inject(Router);
  return auth.autenticado() ? true : router.createUrlTree(['/entrar']);
};

/** Só o DONO: equipe e conexão são configuração, não atendimento.
 *
 *  Defesa em PROFUNDIDADE sobre o 403 da API — a tela nem abre e o link some da sidebar.
 *  O enforcement real continua sendo o [Authorize(Roles="dono")] no controller: guard de
 *  rota é conveniência de UX, não segurança. */
export const guardaDono: CanActivateFn = () => {
  const auth = inject(AuthServico);
  const router = inject(Router);
  return auth.ehDono() ? true : router.createUrlTree(['/caixa']);
};

/** Dono ou gestor. */
export const guardaGestor: CanActivateFn = () => {
  const auth = inject(AuthServico);
  const router = inject(Router);
  return auth.podeGerenciar() ? true : router.createUrlTree(['/caixa']);
};
