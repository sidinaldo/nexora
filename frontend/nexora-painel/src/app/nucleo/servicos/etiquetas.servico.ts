import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EtiquetaDto, EtiquetaNaLista } from '../modelos';

/** O vocabulário de etiquetas da empresa.
 *
 *  ⚠️ LER e ESCREVER têm públicos diferentes, e a API separa por ação:
 *
 *    `listar`  — qualquer papel. É a lista de onde o vendedor escolhe qual aplicar.
 *    o resto   — só o dono. Criar etiqueta é configuração; a API devolve 403 para os outros.
 *
 *  Diferente do `EtapasServico`, em que tudo é do dono. */
@Injectable({ providedIn: 'root' })
export class EtiquetasServico {
  private http = inject(HttpClient);

  /** A lista de gestão, com a contagem de uso. O seletor usa a mesma chamada e ignora a
   *  contagem — um segundo endpoint para poupar sessenta subconsultas indexadas seria
   *  complexidade sem evidência. */
  listar(): Observable<EtiquetaNaLista[]> {
    return this.http.get<EtiquetaNaLista[]>(`${API}/etiquetas`);
  }

  /** Quantos contatos perdem a etiqueta se ela for apagada.
   *
   *  ⚠️ Existe mesmo com a contagem já na lista: a lista foi carregada quando a tela abriu, e
   *  entre aquele instante e o clique em "Apagar" outra pessoa pode ter marcado mais vinte. O
   *  número da confirmação tem de ser o de AGORA. */
  impacto(id: number): Observable<{ contatos: number }> {
    return this.http.get<{ contatos: number }>(`${API}/etiquetas/${id}/impacto`);
  }

  criar(nome: string, cor: string | null): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/etiquetas`, { nome, cor });
  }

  atualizar(id: number, nome: string, cor: string | null): Observable<void> {
    return this.http.put<void>(`${API}/etiquetas/${id}`, { nome, cor });
  }

  /** Sem `destino`, diferente de etapa: apagar etiqueta não deixa contato órfão — ele só perde
   *  um rótulo. Etapa é coluna do kanban, e sem ela o card não tem onde ficar. */
  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/etiquetas/${id}`);
  }

  // ---------------------------------------------------------------- aplicar
  /** As etiquetas de UM contato. Existe separado de `listar()` porque o seletor abre a partir de
   *  três telas, e só uma delas (o detalhe do contato) já tem esse dado carregado. */
  doContato(contatoId: number): Observable<EtiquetaDto[]> {
    return this.http.get<EtiquetaDto[]>(`${API}/contatos/${contatoId}/etiquetas`);
  }

  /** Substitui o conjunto INTEIRO — `PUT`, não `POST`/`DELETE` por etiqueta.
   *
   *  É o que torna a operação idempotente: repetir por duplo clique ou por retry de rede dá o
   *  mesmo resultado. Mesmo argumento que `EtapasServico.reordenar` já usa para a ordem. */
  aplicar(contatoId: number, ids: number[]): Observable<void> {
    return this.http.put<void>(`${API}/contatos/${contatoId}/etiquetas`, { ids });
  }
}
