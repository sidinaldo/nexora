import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { LembreteDto, MeuDia, PaginaDoDia } from '../modelos';

/** O plano do dia e os lembretes manuais.
 *
 *  O Meu Dia NÃO tem tabela: é derivado de conversas esperando resposta + lembretes vencidos.
 *  Responder ou concluir remove a linha sozinho, sem nenhuma sincronização. */
import { PainelServico, recontarPainel } from './painel.servico';

@Injectable({ providedIn: 'root' })
export class MeuDiaServico {
  private http = inject(HttpClient);
  private painel = inject(PainelServico);

  /** `limite` corta a LISTA; `respondendo` e `lembretes` na resposta continuam sendo o total.
   *
   *  O cartão do dashboard pede 6 — antes ele pedia tudo e descartava com `.slice(0, 6)`, o que
   *  fazia uma empresa com 300 conversas esperando baixar 300 para desenhar 6. */
  meuDia(limite?: number): Observable<MeuDia> {
    const params = limite == null ? undefined : new HttpParams().set('limite', limite);
    return this.http.get<MeuDia>(`${API}/meu-dia`, { params });
  }

  /** A tela do Meu Dia: uma página de uma aba, com a ordem e as contagens do servidor (AUD-XX).
   *  O cartão do dashboard continua no `meuDia(limite)` acima. */
  pagina(filtro: string, pagina: number, tamanho: number): Observable<PaginaDoDia> {
    const params = new HttpParams().set('filtro', filtro).set('pagina', pagina).set('tamanho', tamanho);
    return this.http.get<PaginaDoDia>(`${API}/meu-dia/pagina`, { params });
  }

  doContato(contatoId: number): Observable<LembreteDto[]> {
    return this.http.get<LembreteDto[]>(`${API}/lembretes/contato/${contatoId}`);
  }

  criar(corpo: {
    contatoId: number; dataAlvo: string; horaAlvo?: string | null;
    titulo: string; observacao?: string | null;
  }): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/lembretes`, corpo);
  }

  /** ⚠️ `recontarPainel` NAS DUAS. Concluir e cancelar são as únicas operações desta tela que
   *  mudam o contador do menu — sem elas, o número só cairia no ciclo seguinte de 45s. */
  concluir(id: number): Observable<void> {
    return this.http.post<void>(`${API}/lembretes/${id}/concluir`, {})
      .pipe(recontarPainel(this.painel));
  }

  cancelar(id: number): Observable<void> {
    return this.http.post<void>(`${API}/lembretes/${id}/cancelar`, {})
      .pipe(recontarPainel(this.painel));
  }
}
