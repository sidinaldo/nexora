import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { API } from '../api-base';
import { PipelineDto } from '../modelos';

/** OS FUNIS DA EMPRESA — e o estado que o MENU lê.
 *
 *  ===================== POR QUE ESTE SERVIÇO GUARDA ESTADO =====================
 *  Os outros serviços do projeto são só tradutores de HTTP: quem chama guarda o resultado. Este
 *  é diferente porque a lista de pipelines tem DOIS consumidores que não se conhecem — a barra
 *  lateral, que vive pelo tempo todo da sessão, e a tela de gerenciar, que aparece e some.
 *
 *  Sem estado compartilhado, criar uma pipeline na tela de gerenciar não faria o menu mudar até
 *  a próxima recarga da página. Com ele, `recarregar()` atualiza os dois de uma vez.
 *
 *  ⚠️ É o primeiro serviço do projeto com sinal próprio. Se um segundo precisar do mesmo, o
 *  padrão está aqui — não invente outro.
 *  ==============================================================================
 *
 *  ===================== LER É DE TODOS; ESCREVER É DO DONO =====================
 *  `listar` é de qualquer papel: é o menu, e o vendedor precisa navegar entre os quadros. O resto
 *  a API recusa com 403 para quem não é dono. Mesma assimetria das etiquetas.
 *  ============================================================================== */
@Injectable({ providedIn: 'root' })
export class PipelinesServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/pipelines`;

  /** A lista que o menu desenha. Começa vazia; o shell a carrega no boot. */
  readonly lista = signal<PipelineDto[]>([]);

  /** A padrão, ou a primeira — o destino de quem abre `/crm` sem escolher.
   *
   *  ⚠️ ESPELHA A REGRA DO SERVIDOR (`ServicoPipelines.PadraoAsync`) de propósito: a tela precisa
   *  decidir para onde redirecionar ANTES de ter feito qualquer requisição de quadro. Se as duas
   *  divergirem, o sintoma é a tela abrir um funil e a API responder com outro. */
  readonly padrao = computed(() =>
    this.lista().find(p => p.padrao) ?? this.lista()[0] ?? null);

  carregar(): Observable<PipelineDto[]> {
    return this.http.get<PipelineDto[]>(this.base).pipe(tap(l => this.lista.set(l)));
  }

  criar(nome: string, cor: string | null): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, { nome, cor });
  }

  atualizar(id: number, nome: string, cor: string | null): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, { nome, cor });
  }

  /** Rota própria, e não um campo no PUT: muda para onde TODO lead novo vai. */
  definirPadrao(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/padrao`, {});
  }

  /** A API recusa apagar a padrão e a que tem contatos nas etapas. A tela mostra a mensagem
   *  dela em vez de reimplementar a regra — são duas cópias que divergiriam. */
  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }
}
