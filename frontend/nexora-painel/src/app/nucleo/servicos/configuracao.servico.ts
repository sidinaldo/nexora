import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ConfiguracaoEmpresa, FeriadoDto, FusoDisponivel, MinhaConta } from '../modelos';

export interface CorpoAtendimento {
  janelaHoraInicio: number;
  janelaHoraFim: number;
  janelaDiasSemana: number;
  semaforoAmareloMinutos: number;
  semaforoVermelhoMinutos: number;
  diasSemRespostaFollowUp: number;
}

/** Configuração da empresa e da própria conta.
 *
 *  LER é para qualquer papel; ESCREVER a configuração da empresa é só do dono — a API devolve
 *  403 para gestor e vendedor. A tela esconde os botões, mas quem decide é o servidor. */
@Injectable({ providedIn: 'root' })
export class ConfiguracaoServico {
  private http = inject(HttpClient);

  obter(): Observable<ConfiguracaoEmpresa> {
    return this.http.get<ConfiguracaoEmpresa>(`${API}/configuracao`);
  }

  /** Os fusos vêm do SERVIDOR, não de uma constante daqui: a lista depende do tzdata do host, e
   *  oferecer um id que ele não tem levaria o dono direto ao erro de validação. */
  fusos(): Observable<FusoDisponivel[]> {
    return this.http.get<FusoDisponivel[]>(`${API}/configuracao/fusos`);
  }

  ufs(): Observable<string[]> {
    return this.http.get<string[]>(`${API}/configuracao/ufs`);
  }

  /** Nome, documento, fuso e UF.
   *
   *  Trocar o FUSO não reprocessa nada: lembrete e mensagem já reservados mantêm a data que
   *  receberam. A tela avisa. */
  salvarDados(nome: string, documento: string | null,
              fusoHorario: string, uf: string | null): Observable<void> {
    return this.http.put<void>(`${API}/configuracao/empresa`, { nome, documento, fusoHorario, uf });
  }

  /** Janela, faixas do semáforo e dias de follow-up.
   *
   *  NÃO reprocessa o que já foi carimbado: lembrete mantém a data-alvo e mensagem reservada
   *  mantém o dia de disparo. Vale da próxima rodada em diante. As faixas do semáforo, por serem
   *  calculadas no cliente, valem já no próximo /api/painel/status. */
  salvarAtendimento(corpo: CorpoAtendimento): Observable<void> {
    return this.http.put<void>(`${API}/configuracao/atendimento`, corpo);
  }

  // ---------------------------------------------------------------- feriados
  feriados(): Observable<FeriadoDto[]> {
    return this.http.get<FeriadoDto[]>(`${API}/feriados`);
  }

  criarFeriado(data: string, nome: string): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/feriados`, { data, nome });
  }

  /** Só apaga o MANUAL. Nacional é global e devolve 409 — use `trabalhaNoFeriado`. */
  removerFeriado(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/feriados/${id}`);
  }

  trabalhaNoFeriado(id: number): Observable<void> {
    return this.http.post<void>(`${API}/feriados/${id}/trabalha`, {});
  }

  observaFeriado(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/feriados/${id}/trabalha`);
  }

  // ---------------------------------------------------------------- minha conta
  minhaConta(): Observable<MinhaConta> {
    return this.http.get<MinhaConta>(`${API}/conta`);
  }

  salvarMinhaConta(nome: string, email: string): Observable<void> {
    return this.http.put<void>(`${API}/conta`, { nome, email });
  }
}
