import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ChaveOperador } from '../seguranca/chave-operador';
import { CABECALHO_CHAVE_ADMIN } from './cadastro.servico';
import {
  AjusteDeLimites, EditarPlano, EmpresaNaLista, LimitesDaEmpresa, NovoPlano, Pagina, PlanoDto
} from '../modelos';

/** A área do operador. A credencial é a MESMA chave do cadastro de empresa — por isso o nome do
 *  cabeçalho vem de lá, importado, e não reescrito aqui.
 *
 *  ⚠️ DUAS CÓPIAS DESSE LITERAL SERIAM UMA A MAIS. Errar o nome do cabeçalho produz exatamente o
 *  mesmo 401 de chave errada — o servidor responde igual para ausente e inválida, de propósito —,
 *  então a divergência não dedura: o operador passaria a tarde conferindo uma chave correta. */
@Injectable({ providedIn: 'root' })
export class OperadorServico {
  private http = inject(HttpClient);
  private chave = inject(ChaveOperador);

  private get cabecalho() {
    return { headers: { [CABECALHO_CHAVE_ADMIN]: this.chave.atual } };
  }

  // ---- os números ----
  empresas(busca: string, pagina: number, tamanho: number, dias: number): Observable<Pagina<EmpresaNaLista>> {
    const q = `busca=${encodeURIComponent(busca)}&pagina=${pagina}&tamanho=${tamanho}&dias=${dias}`;
    return this.http.get<Pagina<EmpresaNaLista>>(`${API}/operador/empresas?${q}`, this.cabecalho);
  }

  empresa(id: number): Observable<LimitesDaEmpresa> {
    return this.http.get<LimitesDaEmpresa>(`${API}/operador/empresas/${id}`, this.cabecalho);
  }

  // ---- o catálogo ----
  planos(): Observable<PlanoDto[]> {
    return this.http.get<PlanoDto[]>(`${API}/operador/planos`, this.cabecalho);
  }

  criarPlano(novo: NovoPlano): Observable<{ planoId: number }> {
    return this.http.post<{ planoId: number }>(`${API}/operador/planos`, novo, this.cabecalho);
  }

  atualizarPlano(id: number, dados: EditarPlano): Observable<void> {
    return this.http.put<void>(`${API}/operador/planos/${id}`, dados, this.cabecalho);
  }

  // ---- a empresa ----
  atribuirPlano(id: number, planoId: number, confirmarExcedente: boolean): Observable<LimitesDaEmpresa> {
    return this.http.put<LimitesDaEmpresa>(
      `${API}/operador/empresas/${id}/plano`, { planoId, confirmarExcedente }, this.cabecalho);
  }

  ajustarLimites(id: number, ajuste: AjusteDeLimites): Observable<LimitesDaEmpresa> {
    return this.http.put<LimitesDaEmpresa>(
      `${API}/operador/empresas/${id}/limites`, ajuste, this.cabecalho);
  }

  definirAtiva(id: number, ativa: boolean): Observable<LimitesDaEmpresa> {
    return this.http.put<LimitesDaEmpresa>(
      `${API}/operador/empresas/${id}/ativa`, { ativa }, this.cabecalho);
  }
}
