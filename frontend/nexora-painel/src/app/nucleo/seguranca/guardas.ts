import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthServico } from '../servicos/auth.servico';
import { Permissao } from '../modelos';

/** Sem sessão, não entra. */
export const guardaAutenticado: CanActivateFn = () => {
  const auth = inject(AuthServico);
  const router = inject(Router);
  return auth.autenticado() ? true : router.createUrlTree(['/entrar']);
};

/** A tela só abre para quem pode o GESTO dela — `configurar_empresa`, `gerenciar_equipe`.
 *
 *  Defesa em PROFUNDIDADE sobre o 403 da API — a tela nem abre e o link some da sidebar. O
 *  enforcement real é a política da rota no servidor, e as duas leem a MESMA tabela
 *  (`Seguranca.Permissoes`): aqui não há papel nenhum.
 *
 *  Era `guardaDono`, com `papel === 'dono'` — uma segunda cópia da regra, escrita à mão.
 *
 *  ⚠️ SESSÃO SEM A LISTA (aberta antes de ela existir): em vez de barrar — o dono cairia na
 *  caixa ao abrir o link salvo de Equipe —, pergunta ao servidor e decide com a resposta. */
export function guardaPermissao(permissao: Permissao): CanActivateFn {
  return () => {
    const auth = inject(AuthServico);
    const router = inject(Router);
    const barrar = router.createUrlTree(['/caixa']);

    if (auth.permissoesConhecidas()) return auth.pode(permissao) ? true : barrar;
    if (!auth.autenticado()) return barrar;

    return auth.atualizarPermissoes().pipe(
      map(() => auth.pode(permissao) ? true : barrar),
      catchError(() => of(barrar)));
  };
}
