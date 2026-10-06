import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela, totalDePaginas }
  from '../../nucleo/paginacao/paginacao';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import {
  JanelaDeParada, LeadParado, LeadsParadosServico
} from '../../nucleo/servicos/leads-parados.servico';
import { OpcoesRelatorio, RelatoriosServico } from '../../nucleo/servicos/relatorios.servico';

/** ===================== LEADS PARADOS (LPA-1) =====================
 *
 *  O lead que chegou, teve uma conversa e esfriou fica no funil para sempre, indistinguível do que
 *  foi atendido ontem. Até aqui, descobrir isso exigia abrir contato por contato.
 *
 *  ⚠️ ESTA ENTREGA É SÓ LEITURA. As ações em lote vêm depois, e é lá que nasce a permissão própria
 *  — ver não é agir, e antecipar o gesto fecharia a tela para quem ela mais ajuda.
 *
 *  ⚠️ O RECORTE POR PESSOA NÃO ESTÁ AQUI. Quem não tem `ver_numeros_da_equipe` recebe só os
 *  próprios leads, decidido no servidor. A tela não esconde nada: ela não recebe. É por isso que a
 *  rota não tem guarda — o vendedor abre e vê a lista de trabalho dele.
 *
 *  O seletor de responsável vem travado para o vendedor porque `/relatorios/opcoes` devolve só ele
 *  — não porque a tela decide.
 *  ============================================================== */
@Component({
  selector: 'app-leads-parados',
  imports: [FormsModule, RouterLink, DecimalPipe, Paginacao],
  templateUrl: './leads-parados.html',
  styleUrl: './leads-parados.css'
})
export class LeadsParados implements OnInit {
  private api = inject(LeadsParadosServico);
  private relatorios = inject(RelatoriosServico);
  auth = inject(AuthServico);

  readonly janelas = this.api.janelas;
  readonly tamanho = this.api.porPagina;

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  dias = signal<JanelaDeParada>(30);
  responsavelId = signal<number | null>(null);

  itens = signal<LeadParado[]>([]);
  total = signal(0);
  pagina = signal(1);
  carregando = signal(false);
  erro = signal('');

  opcoes = signal<OpcoesRelatorio>({ responsaveis: [], etapas: [], motivosPerda: [] });

  totalPaginas = computed(() => totalDePaginas(this.total(), this.tamanho));

  /** Reserva a altura para o rodapé de paginação não pular entre uma página cheia e a última. */
  alturaMinima = computed(() =>
    this.totalPaginas() > 1 ? alturaMinimaDaTabela(this.tamanho) : 0);

  /** O texto do vazio é BIFURCADO: "ninguém parado" e "ninguém parado com esse recorte" mandam a
   *  pessoa fazer coisas diferentes. Dizer só o primeiro com um filtro ligado esconde o filtro. */
  temFiltro = computed(() => this.responsavelId() !== null || this.dias() !== 30);

  ngOnInit() {
    // ⚠️ REAPROVEITA `/relatorios/opcoes`, que já devolve a lista de responsáveis RECORTADA pelo
    // papel de quem pergunta. Um endpoint novo seria uma segunda fonte da mesma lista, e o dia em
    // que o recorte mudasse num só deixaria a outra tela oferecendo gente que ela não pode ver.
    this.relatorios.opcoes().subscribe({
      next: o => this.opcoes.set(o),
      // Seletor vazio é menos ruim que a tela não abrir: a lista não depende dele para funcionar.
      error: () => { }
    });

    this.carregar();
  }

  carregar() {
    this.carregando.set(true);

    this.api.listar(this.dias(), this.responsavelId(), this.pagina()).subscribe({
      next: p => {
        this.itens.set(p.itens);
        this.total.set(p.total);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar os leads parados. Tente de novo.');
        this.carregando.set(false);
      }
    });
  }

  /** Todo filtro volta para a página 1: continuar na página 7 de um recorte que agora tem duas
   *  páginas devolveria uma lista vazia que parece "não há nada". */
  private doZero() {
    this.pagina.set(1);
    this.carregar();
  }

  trocarJanela(dias: JanelaDeParada) {
    if (dias === this.dias()) return;

    this.dias.set(dias);
    this.doZero();
  }

  trocarResponsavel(valor: string) {
    this.responsavelId.set(valor ? Number(valor) : null);
    this.doZero();
  }

  limpar() {
    this.dias.set(30);
    this.responsavelId.set(null);
    this.doZero();
  }

  irPara(p: number) {
    if (p < 1 || p > this.totalPaginas()) return;

    this.pagina.set(p);
    this.carregar();
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  /** "há 3 meses" responde a pergunta; "há 97 dias" é preciso e não responde. */
  tempoParado(dias: number): string {
    if (dias < 1) return 'hoje';
    if (dias === 1) return 'ontem';
    if (dias < 30) return `há ${dias} dias`;

    const meses = Math.floor(dias / 30);

    return meses === 1 ? 'há 1 mês' : `há ${meses} meses`;
  }
}
